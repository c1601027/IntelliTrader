﻿using IntelliTrader.Core;
using IntelliTrader.Exchange.Base;
using IntelliTrader.Signals.Base;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace IntelliTrader.Trading
{
    internal class TradingService : ConfigrableServiceBase<TradingConfig>, ITradingService
    {
        private const int MIN_INTERVAL_BETWEEN_BUY_AND_SELL = 10000;
        private const decimal DEFAULT_ARBITRAGE_BUY_MULTIPLIER = 0.99M;

        public override string ServiceName => Constants.ServiceNames.TradingService;

        ITradingConfig ITradingService.Config => Config;
        public IModuleRules Rules { get; private set; }
        public TradingRulesConfig RulesConfig { get; private set; }

        public IExchangeService Exchange { get; private set; }
        public ITradingAccount Account { get; private set; }
        public ConcurrentStack<IOrderDetails> OrderHistory { get; private set; } = new ConcurrentStack<IOrderDetails>();
        public bool IsTradingSuspended { get; private set; }

        private readonly ILoggingService loggingService;
        private readonly INotificationService notificationService;
        private readonly IHealthCheckService healthCheckService;
        private readonly ITasksService tasksService;
        private IOrderingService orderingService;
        private IRulesService rulesService;
        private ISignalsService signalsService;

        private TradingTimedTask tradingTimedTask;
        private TradingRulesTimedTask tradingRulesTimedTask;
        private AccountRefreshTimedTask accountRefreshTimedTask;

        private bool tradingForcefullySuspended;
        private object syncRoot = new object();

        public TradingService(ILoggingService loggingService, INotificationService notificationService, IHealthCheckService healthCheckService, ITasksService tasksService)
        {
            this.loggingService = loggingService;
            this.notificationService = notificationService;
            this.healthCheckService = healthCheckService;
            this.tasksService = tasksService;

            var isBacktesting = Application.Resolve<IBacktestingService>().Config.Enabled && Application.Resolve<IBacktestingService>().Config.Replay;
            if (isBacktesting)
            {
                this.Exchange = Application.ResolveOptionalNamed<IExchangeService>(Constants.ServiceNames.BacktestingExchangeService);
            }
            else
            {
                this.Exchange = Application.ResolveOptionalNamed<IExchangeService>(Config.Exchange);
            }

            if (this.Exchange == null)
            {
                throw new Exception($"Unsupported exchange: {Config.Exchange}");
            }
        }

        public void Start()
        {
            loggingService.Info($"Start Trading service (Virtual: {Config.VirtualTrading})...");

            IsTradingSuspended = true;

            orderingService = Application.Resolve<IOrderingService>();
            rulesService = Application.Resolve<IRulesService>();
            OnTradingRulesChanged();
            rulesService.RegisterRulesChangeCallback(OnTradingRulesChanged);
            Exchange.Start(Config.VirtualTrading);
            signalsService = Application.Resolve<ISignalsService>();

            if (!Config.VirtualTrading)
            {
                Account = new ExchangeAccount(loggingService, notificationService, healthCheckService, signalsService, this);
            }
            else
            {
                Account = new VirtualAccount(loggingService, notificationService, healthCheckService, signalsService, this);
            }

            accountRefreshTimedTask = tasksService.AddTask(
                name: nameof(AccountRefreshTimedTask),
                task: new AccountRefreshTimedTask(loggingService, healthCheckService, this),
                interval: Config.AccountRefreshInterval * 1000 / Application.Speed,
                startDelay: Constants.TaskDelays.ZeroDelay,
                startTask: false,
                runNow: true,
                skipIteration: 0);

            if (signalsService.Config.Enabled)
            {
                signalsService.Start();
            }

            tradingTimedTask = tasksService.AddTask(
                name: nameof(TradingTimedTask),
                task: new TradingTimedTask(loggingService, notificationService, healthCheckService, signalsService, orderingService, this),
                interval: Config.TradingCheckInterval * 1000 / Application.Speed,
                startDelay: Constants.TaskDelays.NormalDelay,
                startTask: false,
                runNow: false,
                skipIteration: 0);

            tradingRulesTimedTask = tasksService.AddTask(
                name: nameof(TradingRulesTimedTask),
                task: new TradingRulesTimedTask(loggingService, notificationService, healthCheckService, rulesService, signalsService, this),
                interval: RulesConfig.CheckInterval * 1000 / Application.Speed,
                startDelay: Constants.TaskDelays.MidDelay,
                startTask: false,
                runNow: false,
                skipIteration: 0);

            IsTradingSuspended = false;

            loggingService.Info("Trading service started");
        }

        public void Stop()
        {
            loggingService.Info("Stop Trading service...");

            Exchange.Stop();

            if (signalsService.Config.Enabled)
            {
                signalsService.Stop();
            }

            tasksService.RemoveTask(nameof(TradingTimedTask), stopTask: true);
            tasksService.RemoveTask(nameof(TradingRulesTimedTask), stopTask: true);
            tasksService.RemoveTask(nameof(AccountRefreshTimedTask), stopTask: true);

            Account.Dispose();

            rulesService.UnregisterRulesChangeCallback(OnTradingRulesChanged);

            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.TradingRulesProcessed);
            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.TradingPairsProcessed);

            loggingService.Info("Trading service stopped");
        }

        public void ResumeTrading(bool forced)
        {
            if (IsTradingSuspended && (!tradingForcefullySuspended || forced))
            {
                loggingService.Info("Trading started");
                IsTradingSuspended = false;

                tradingTimedTask.Start();
                tradingRulesTimedTask.Start();
                tradingRulesTimedTask.RunNow();
            }
        }

        public void SuspendTrading(bool forced)
        {
            if (!IsTradingSuspended)
            {
                loggingService.Info("Trading suspended");
                IsTradingSuspended = true;
                tradingForcefullySuspended = forced;

                tradingRulesTimedTask.Stop();
                tradingTimedTask.Stop();
                tradingTimedTask.StopTrailing();
            }
        }

        public IPairConfig GetPairConfig(string pair)
        {
            return tradingRulesTimedTask.GetPairConfig(pair);
        }

        public void ReapplyTradingRules()
        {
            tradingRulesTimedTask.RunNow();
        }

        public void Buy(BuyOptions options)
        {
            lock (syncRoot)
            {
                PauseTasks();
                try
                {
                    IRule rule = signalsService.Rules.Entries.FirstOrDefault(r => r.Name == options.Metadata.SignalRule);
                    RuleAction ruleAction = rule?.Action ?? RuleAction.Default;
                    IPairConfig pairConfig = GetPairConfig(options.Pair);

                    bool arbitragePair = pairConfig.ArbitrageEnabled && pairConfig.ArbitrageSignalRules.Contains(options.Metadata.SignalRule);
                    if (arbitragePair)
                    {
                        Arbitrage arbitrage = Exchange.GetArbitrage(options.Pair, Config.Market, pairConfig.ArbitrageMarkets, pairConfig.ArbitrageType);
                        if (arbitrage.IsAssigned)
                        {
                            if (arbitrage.Type == ArbitrageType.Direct)
                            {
                                ArbitrageDirect(new ArbitrageOptions(options.Pair, arbitrage, options.Metadata));
                            }
                            else if (arbitrage.Type == ArbitrageType.Reverse)
                            {
                                ArbitrageReverse(new ArbitrageOptions(options.Pair, arbitrage, options.Metadata));
                            }
                        }
                    }
                    else
                    {
                        ITradingPair swappedPair = Account.GetTradingPairs().OrderBy(p => p.CurrentMargin).FirstOrDefault(tradingPair =>
                        {
                            IPairConfig tradingPairConfig = GetPairConfig(tradingPair.Pair);
                            return tradingPairConfig.SellEnabled && tradingPairConfig.SwapEnabled && tradingPairConfig.SwapSignalRules != null &&
                                   tradingPairConfig.SwapSignalRules.Contains(options.Metadata.SignalRule) &&
                                   tradingPairConfig.SwapTimeout < (DateTimeOffset.Now - tradingPair.OrderDates.Max()).TotalSeconds;
                        });

                        if (swappedPair != null)
                        {
                            Swap(new SwapOptions(swappedPair.Pair, options.Pair, options.Metadata));
                        }
                        else if (ruleAction == RuleAction.Default)
                        {
                            if (CanBuy(options, out string message))
                            {
                                tradingTimedTask.InitiateBuy(options);
                            }
                            else
                            {
                                loggingService.Debug(message);
                            }
                        }
                    }
                }
                finally
                {
                    ContinueTasks();
                }
            }
        }

        public void Sell(SellOptions options)
        {
            lock (syncRoot)
            {
                PauseTasks();
                try
                {
                    if (CanSell(options, out string message))
                    {
                        tradingTimedTask.InitiateSell(options);
                    }
                    else
                    {
                        loggingService.Debug(message);
                    }
                }
                finally
                {
                    ContinueTasks();
                }
            }
        }

        public void Swap(SwapOptions options)
        {
            lock (syncRoot)
            {
                PauseTasks();
                try
                {
                    if (CanSwap(options, out string message))
                    {
                        ITradingPair oldTradingPair = Account.GetTradingPair(options.OldPair);
                        var sellOptions = new SellOptions(options.OldPair)
                        {
                            Swap = true,
                            ManualOrder = options.ManualOrder,
                            Metadata = new OrderMetadata { SwapPair = options.NewPair }
                        };

                        if (CanSell(sellOptions, out message))
                        {
                            decimal currentMargin = oldTradingPair.CurrentMargin;
                            decimal additionalCosts = oldTradingPair.ActualCost - oldTradingPair.CurrentCost + (oldTradingPair.Metadata.AdditionalCosts ?? 0);
                            int additionalDCALevels = oldTradingPair.DCALevel;

                            IOrderDetails sellOrderDetails = orderingService.PlaceSellOrder(sellOptions);
                            if (!Account.HasTradingPair(options.OldPair))
                            {
                                var buyOptions = new BuyOptions(options.NewPair)
                                {
                                    Swap = true,
                                    ManualOrder = options.ManualOrder,
                                    MaxCost = sellOrderDetails.RawCost,
                                    Metadata = options.Metadata
                                };
                                buyOptions.Metadata.LastBuyMargin = currentMargin;
                                buyOptions.Metadata.SwapPair = options.OldPair;
                                buyOptions.Metadata.AdditionalDCALevels = additionalDCALevels;
                                buyOptions.Metadata.AdditionalCosts = additionalCosts;
                                IOrderDetails buyOrderDetails = orderingService.PlaceBuyOrder(buyOptions);

                                var newTradingPair = Account.GetTradingPair(options.NewPair) as TradingPair;
                                if (newTradingPair != null)
                                {
                                    newTradingPair.Metadata.AdditionalCosts += sellOrderDetails.Fees;
                                    loggingService.Info($"Swap {oldTradingPair.FormattedName} for {newTradingPair.FormattedName}. " +
                                        $"Old margin: {oldTradingPair.CurrentMargin:0.00}, new margin: {newTradingPair.CurrentMargin:0.00}");
                                }
                                else
                                {
                                    loggingService.Info($"Unable to swap {options.OldPair} for {options.NewPair}. Reason: failed to buy {options.NewPair}");
                                    notificationService.Notify($"Unable to swap {options.OldPair} for {options.NewPair}: Failed to buy {options.NewPair}");
                                }
                            }
                            else
                            {
                                loggingService.Info($"Unable to swap {options.OldPair} for {options.NewPair}. Reason: failed to sell {options.OldPair}");
                            }
                        }
                        else
                        {
                            loggingService.Info($"Unable to swap {options.OldPair} for {options.NewPair}: {message}");
                        }
                    }
                    else
                    {
                        loggingService.Info(message);
                    }
                }
                finally
                {
                    ContinueTasks();
                }
            }
        }

        public void ArbitrageDirect(ArbitrageOptions options)
        {
            lock (syncRoot)
            {
                PauseTasks();
                try
                {
                    if (CanArbitrage(options, out string message))
                    {
                        throw new NotSupportedException();
                    }
                    else
                    {
                        loggingService.Info(message);
                    }
                }
                finally
                {
                    ContinueTasks();
                }
            }
        }

        public void ArbitrageReverse(ArbitrageOptions options)
        {
            lock (syncRoot)
            {
                PauseTasks();
                try
                {
                    if (CanArbitrage(options, out string message))
                    {
                        if (CanBuy(new BuyOptions(options.Pair) { Amount = 1 }, out message))
                        {
                            IPairConfig pairConfig = GetPairConfig(options.Pair);
                            options.Metadata.Arbitrage = $"{options.Arbitrage.Market}-" + options.Arbitrage.Type ?? "All";
                            options.Metadata.ArbitragePercentage = options.Arbitrage.Percentage;
                            loggingService.Info($"{options.Arbitrage.Type} arbitrage {options.Pair} on {options.Arbitrage.Market}. Percentage: {options.Arbitrage.Percentage:0.00}");

                            bool useExistingMarketPair = false;
                            string marketPair = Exchange.GetArbitrageMarketPair(options.Arbitrage.Market);
                            ITradingPair existingMarketPair = Account.GetTradingPair(marketPair);
                            if (existingMarketPair != null &&
                                existingMarketPair.CurrentCost > pairConfig.BuyMaxCost &&
                                existingMarketPair.AveragePrice <= existingMarketPair.CurrentPrice)
                            {
                                useExistingMarketPair = true;
                            }

                            var buyMarketPairOptions = new BuyOptions(marketPair)
                            {
                                Arbitrage = true,
                                MaxCost = pairConfig.BuyMaxCost,
                                ManualOrder = options.ManualOrder,
                                IgnoreBalance = useExistingMarketPair,
                                Metadata = options.Metadata
                            };

                            if (CanBuy(buyMarketPairOptions, out message))
                            {
                                IOrderDetails buyMarketPairOrderDetails = null;
                                if (useExistingMarketPair)
                                {
                                    buyMarketPairOrderDetails = Account.AddBlankOrder(buyMarketPairOptions.Pair,
                                        buyMarketPairOptions.MaxCost.Value / GetPrice(buyMarketPairOptions.Pair, TradePriceType.Ask),
                                        includeFees: false);
                                }
                                else
                                {
                                    buyMarketPairOrderDetails = orderingService.PlaceBuyOrder(buyMarketPairOptions);
                                }

                                if (buyMarketPairOrderDetails.Result == OrderResult.Filled)
                                {
                                    decimal buyMarketPairFees = CalculateOrderFees(buyMarketPairOrderDetails);
                                    string arbitragePair = Exchange.ChangeMarket(options.Pair, options.Arbitrage.Market.ToString());
                                    decimal buyArbitragePairAmount = options.Arbitrage.Market == ArbitrageMarket.USDT ?
                                        buyMarketPairOrderDetails.AmountFilled * buyMarketPairOrderDetails.AveragePrice / GetPrice(arbitragePair, TradePriceType.Ask) :
                                        buyMarketPairOrderDetails.AmountFilled / GetPrice(arbitragePair, TradePriceType.Ask);

                                    var buyArbitragePairOptions = new BuyOptions(arbitragePair)
                                    {
                                        Arbitrage = true,
                                        ManualOrder = options.ManualOrder,
                                        Amount = buyArbitragePairAmount * (pairConfig.ArbitrageBuyMultiplier ?? DEFAULT_ARBITRAGE_BUY_MULTIPLIER),
                                        Metadata = options.Metadata
                                    };

                                    IOrderDetails buyArbitragePairOrderDetails = orderingService.PlaceBuyOrder(buyArbitragePairOptions);
                                    if (buyArbitragePairOrderDetails.Result == OrderResult.Filled)
                                    {
                                        decimal buyArbitragePairFees = CalculateOrderFees(buyArbitragePairOrderDetails);
                                        var sellArbitragePairOptions = new SellOptions(buyArbitragePairOrderDetails.Pair)
                                        {
                                            Arbitrage = true,
                                            Amount = buyArbitragePairOptions.Amount,
                                            ManualOrder = options.ManualOrder,
                                            Metadata = options.Metadata
                                        };

                                        TradingPair existingArbitragePair = Account.GetTradingPair(buyArbitragePairOrderDetails.Pair) as TradingPair;
                                        existingArbitragePair.FeesNonDeductible = buyArbitragePairFees;
                                        existingArbitragePair.OverrideActualCost(buyArbitragePairOrderDetails.RawCost + buyMarketPairFees + buyArbitragePairFees * 2);
                                        IOrderDetails sellArbitragePairOrderDetails = orderingService.PlaceSellOrder(sellArbitragePairOptions);
                                        existingArbitragePair.OverrideActualCost(null);

                                        if (sellArbitragePairOrderDetails.Result == OrderResult.Filled)
                                        {
                                            loggingService.Info($"{pairConfig.ArbitrageType} arbitrage successful: {marketPair} -> {arbitragePair} -> {existingArbitragePair.Pair}");
                                        }
                                        else
                                        {
                                            loggingService.Info($"Unable to arbitrage {options.Pair}. Reason: failed to sell arbitrage pair {arbitragePair}");
                                            notificationService.Notify($"Unable to arbitrage {options.Pair}: Failed to sell arbitrage pair {arbitragePair}");
                                        }
                                    }
                                    else
                                    {
                                        loggingService.Info($"Unable to arbitrage {options.Pair}. Reason: failed to buy arbitrage pair {arbitragePair}");
                                        notificationService.Notify($"Unable to arbitrage {options.Pair}: Failed to buy arbitrage pair {arbitragePair}");
                                    }
                                }
                                else
                                {
                                    loggingService.Info($"Unable to arbitrage {options.Pair}. Reason: failed to buy market pair {marketPair}");
                                }
                            }
                            else
                            {
                                loggingService.Info($"Unable to arbitrage {options.Pair}: {message}");
                            }
                        }
                        else
                        {
                            loggingService.Info($"Unable to arbitrage {options.Pair}: {message}");
                        }
                    }
                    else
                    {
                        loggingService.Info(message);
                    }
                }
                finally
                {
                    ContinueTasks();
                }
            }
        }

        public bool CanBuy(BuyOptions options, out string message)
        {
            IPairConfig pairConfig = GetPairConfig(options.Pair);

            if (!options.ManualOrder && !options.Swap && IsTradingSuspended)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: trading suspended";
                return false;
            }
            else if (!options.ManualOrder && !options.Swap && !pairConfig.BuyEnabled)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: buying not enabled";
                return false;
            }
            else if (!options.ManualOrder && Config.ExcludedPairs.Contains(options.Pair))
            {
                message = $"Cancel buy request for {options.Pair}. Reason: exluded pair";
                return false;
            }
            else if (!options.ManualOrder && !options.Arbitrage && !options.IgnoreExisting && Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel buy request for {options.Pair}. Reason: pair already exists";
                return false;
            }
            else if (!options.ManualOrder && !options.Swap && !options.Arbitrage && pairConfig.MaxPairs != 0 && Account.GetTradingPairs().Count() >= pairConfig.MaxPairs && !Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel buy request for {options.Pair}. Reason: maximum pairs reached";
                return false;
            }
            else if (!options.ManualOrder && !options.Swap && !options.IgnoreBalance && pairConfig.BuyMinBalance != 0 && (Account.GetBalance() - options.MaxCost) < pairConfig.BuyMinBalance && Exchange.GetPairMarket(options.Pair) == Config.Market)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: minimum balance reached";
                return false;
            }
            else if (GetPrice(options.Pair) <= 0)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: invalid price";
                return false;
            }
            else if (!options.IgnoreBalance && Account.GetBalance() < options.MaxCost && Exchange.GetPairMarket(options.Pair) == Config.Market)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: not enough balance";
                return false;
            }
            else if (options.Amount == null && options.MaxCost == null || options.Amount != null && options.MaxCost != null)
            {
                message = $"Cancel buy request for {options.Pair}. Reason: either max cost or amount needs to be specified (not both)";
            }
            else if (!options.ManualOrder && !options.Swap && !options.Arbitrage && pairConfig.BuySamePairTimeout > 0 && 
                OrderHistory.Any(h => h.Side == OrderSide.Buy && (h.Pair == options.Pair || h.Pair == h.OriginalPair)) &&
                (DateTimeOffset.Now - OrderHistory.Where(h => (h.Pair == options.Pair || h.Pair == h.OriginalPair)).Max(h => h.Date)).TotalSeconds < pairConfig.BuySamePairTimeout)
            {
                var elapsedSeconds = (DateTimeOffset.Now - OrderHistory.Where(h => (h.Pair == options.Pair || h.Pair == h.OriginalPair)).Max(h => h.Date)).TotalSeconds;
                message = $"Cancel buy request for {options.Pair}. Reason: buy same pair timeout (elapsed: {elapsedSeconds:0.#}, timeout: {pairConfig.BuySamePairTimeout:0.#})";
                return false;
            }

            message = null;
            return true;
        }

        public bool CanSell(SellOptions options, out string message)
        {
            IPairConfig pairConfig = GetPairConfig(options.Pair);

            if (!options.ManualOrder && IsTradingSuspended)
            {
                message = $"Cancel sell request for {options.Pair}. Reason: trading suspended";
                return false;
            }
            else if (!options.ManualOrder && !pairConfig.SellEnabled)
            {
                message = $"Cancel sell request for {options.Pair}. Reason: selling not enabled";
                return false;
            }
            else if (!options.ManualOrder && Config.ExcludedPairs.Contains(options.Pair))
            {
                message = $"Cancel sell request for {options.Pair}. Reason: excluded pair";
                return false;
            }
            else if (!Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel sell request for {options.Pair}. Reason: pair does not exist";
                return false;
            }
            else if (GetPrice(options.Pair) <= 0)
            {
                message = $"Cancel sell request for {options.Pair}. Reason: invalid price";
                return false;
            }
            else if (!options.Arbitrage && (DateTimeOffset.Now - Account.GetTradingPair(options.Pair).OrderDates.Max()).TotalMilliseconds < (MIN_INTERVAL_BETWEEN_BUY_AND_SELL / Application.Speed))
            {
                message = $"Cancel sell request for {options.Pair}. Reason: pair just bought";
                return false;
            }
            message = null;
            return true;
        }

        public bool CanSwap(SwapOptions options, out string message)
        {
            if (!Account.HasTradingPair(options.OldPair))
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: pair does not exist";
                return false;
            }
            else if (Account.HasTradingPair(options.NewPair))
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: pair already exists";
                return false;
            }
            else if (!options.ManualOrder && !GetPairConfig(options.OldPair).SellEnabled)
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: selling not enabled";
                return false;
            }
            else if (!options.ManualOrder && !GetPairConfig(options.NewPair).BuyEnabled)
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: buying not enabled";
                return false;
            }
            else if (Account.GetBalance() < Account.GetTradingPair(options.OldPair).CurrentCost * 0.01M)
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: not enough balance";
                return false;
            }
            else if (!Exchange.GetMarketPairs(Config.Market).Contains(options.NewPair))
            {
                message = $"Cancel swap request {options.OldPair} for {options.NewPair}. Reason: {options.NewPair} is not a valid pair";
                return false;
            }

            message = null;
            return true;
        }

        public bool CanArbitrage(ArbitrageOptions options, out string message)
        {
            if (Account.HasTradingPair(options.Pair))
            {
                message = $"Cancel arbitrage request {options.Pair}. Reason: pair already exist";
                return false;
            }
            else if (!options.ManualOrder && !GetPairConfig(options.Pair).BuyEnabled)
            {
                message = $"Cancel arbitrage request for {options.Pair}. Reason: buying not enabled";
                return false;
            }
            else if (!Exchange.GetMarketPairs(Config.Market).Contains(options.Pair))
            {
                message = $"Cancel arbitrage request for {options.Pair}. Reason: {options.Pair} is not a valid pair";
                return false;
            }

            message = null;
            return true;
        }

        public decimal GetPrice(string pair, TradePriceType? priceType = null)
        {
            return Exchange.GetPrice(pair, priceType ?? Config.TradePriceType);
        }

        public decimal CalculateOrderFees(IOrderDetails order)
        {
            if (order.Fees != 0 && order.FeesCurrency != null)
            {
                if (order.FeesCurrency == Config.Market)
                {
                    return order.Fees;
                }
                else
                {
                    string feesPair = order.FeesCurrency + Config.Market;
                    return GetPrice(feesPair, TradePriceType.Ask) * order.Fees;
                }
            }
            else
            {
                return 0;
            }
        }

        public bool IsNormalizedPair(string pair)
        {
            string pairMarket = Exchange.GetPairMarket(pair);
            return pairMarket == Config.Market || pairMarket == Constants.Markets.USDT;
        }

        public string NormalizePair(string pair)
        {
            return Exchange.ChangeMarket(pair, Config.Market);
        }

        public void LogOrder(IOrderDetails order)
        {
            OrderHistory.Push(order);
        }

        public List<string> GetTrailingBuys()
        {
            return tradingTimedTask.GetTrailingBuys();
        }

        public List<string> GetTrailingSells()
        {
            return tradingTimedTask.GetTrailingSells();
        }

        public void StopTrailingBuy(string pair)
        {
            tradingTimedTask.StopTrailingBuy(pair);
        }

        public void StopTrailingSell(string pair)
        {
            tradingTimedTask.StopTrailingSell(pair);
        }

        private void OnTradingRulesChanged()
        {
            Rules = rulesService.GetRules(ServiceName);
            RulesConfig = Rules.GetConfiguration<TradingRulesConfig>();
        }

        protected override void PrepareConfig()
        {
            if (Config.ExcludedPairs == null)
            {
                Config.ExcludedPairs = new List<string>();
            }

            if (Config.DCALevels == null)
            {
                Config.DCALevels = new List<DCALevel>();
            }
        }

        private void PauseTasks()
        {
            tasksService.GetTask(nameof(TradingTimedTask)).Pause();
            tasksService.GetTask(nameof(TradingRulesTimedTask)).Pause();
            tasksService.GetTask(nameof(SignalRulesTimedTask)).Pause();
            tasksService.GetTask("BacktestingLoadSnapshotsTimedTask")?.Pause();
        }

        private void ContinueTasks()
        {
            tasksService.GetTask(nameof(TradingTimedTask)).Continue();
            tasksService.GetTask(nameof(TradingRulesTimedTask)).Continue();
            tasksService.GetTask(nameof(SignalRulesTimedTask)).Continue();
            tasksService.GetTask("BacktestingLoadSnapshotsTimedTask")?.Continue();
        }
    }
}
