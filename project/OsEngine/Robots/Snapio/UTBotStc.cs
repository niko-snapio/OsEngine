using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Logging;
using System.Linq;
using System.Windows.Documents;
using OsEngine.Alerts;

/* Description
trading robot for osengine

The Counter trend robot on Ema with ATR

Buy:
1. The volume is above the VolumeValue.
2. Candle falling.
3. The price is below Ema.
4. The value of Atr is higher than the average value for a certain period (CandlesCountAtr) by MultAtr times.

Sell:
1. The volume is above the VolumeValue.
2. Candle growing.
3. The price is higher than Ema.
4. The value of Atr is higher than the average value for a certain period (CandlesCountAtr) by MultAtr times.

Exit from buy: Trailing stop is placed at the minimum for the period specified for the
trailing stop and is transferred (sliding) to new price lows, also for the specified period.
Exit from sell: Trailing stop is placed on the maximum for the period specified for
the trailing stop and is transferred (sliding) to a new price maximum, also for the specified period.

 */


namespace OsEngine.Robots.Snapio
{
    [Bot("UTBotStc")] // We create an attribute so that we don't write anything to the BotFactory
    public class UTBotStc : BotPanel
    {
        private BotTabSimple _tab;

        // Basic Settings
        private StrategyParameterString Regime;
        private StrategyParameterString VolumeRegime;
        private StrategyParameterDecimal VolumeOnPosition;
        private StrategyParameterDecimal Slippage;

        // Indicator setting 
        private StrategyParameterInt PeriodEma;
        private StrategyParameterInt AtrPeriod;
        private StrategyParameterDecimal KeyValue;

        // Indicator
        Aindicator _Ema;
        Aindicator _ATR;
        private Aindicator _macd;

        // The last value of the indicator
        private decimal _lastATR;
        private decimal _lastEma;
        private decimal _lastPrice;
        private decimal _prevPrice;
        private decimal _xATRTrailingStop = 0;
        private decimal _lastMacdUp;
        private decimal _lastMacdDown;
        private bool _isLongEnabled;
        private bool _isShortEnabled;
        private decimal _lastNloss { get { return _lastATR * KeyValue.ValueDecimal; } }


        // The prev value of the indicator
        private decimal _prevVolume;

        // Exit
        private StrategyParameterInt TrailCandlesLong;
        private StrategyParameterInt TrailCandlesShort;

        public UTBotStc(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            // Basic setting
            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort", "OnlyClosePosition" }, "Base");
            VolumeRegime = CreateParameter("Volume type", "Number of contracts", new[] { "Number of contracts", "Contract currency" }, "Base");
            VolumeOnPosition = CreateParameter("Volume", 1, 1.0m, 50, 4, "Base");
            Slippage = CreateParameter("Slippage %", 0m, 0, 20, 1, "Base");

            // Indicator setting
            AtrPeriod = CreateParameter("ATR Period", 14, 7, 48, 7, "Base");
            KeyValue = CreateParameter("Key Vaule. 'This changes the sensitivity'", 1.5m, 1m, 10, 0.1m, "Base");


            PeriodEma = CreateParameter("EMA Period", 50, 10, 300, 10, "Base");

            // Create indicator Ema
            _Ema = IndicatorsFactory.CreateIndicatorByName("Ema", name + "Ema", false);
            _Ema = (Aindicator)_tab.CreateCandleIndicator(_Ema, "Prime");
            ((IndicatorParameterInt)_Ema.Parameters[0]).ValueInt = PeriodEma.ValueInt;
            _Ema.Save();

            // Create indicator ATR
            _ATR = IndicatorsFactory.CreateIndicatorByName("ATR", name + "Atr", false);
            _ATR = (Aindicator)_tab.CreateCandleIndicator(_ATR, "AtrArea");
            ((IndicatorParameterInt)_ATR.Parameters[0]).ValueInt = AtrPeriod.ValueInt;
            _ATR.Save();

            _macd = IndicatorsFactory.CreateIndicatorByName("MACD", name + "MACD", false);
            _macd = (Aindicator)_tab.CreateCandleIndicator(_macd, "MacdArea");
            ((IndicatorParameterInt)_macd.Parameters[0]).ValueInt = AtrPeriod.ValueInt;
            _macd.Save();

            // Exit
            TrailCandlesLong = CreateParameter("Trail Candles Long", 5, 5, 200, 5, "Exit");
            TrailCandlesShort = CreateParameter("Trail Candles Short", 5, 5, 200, 5, "Exit");

            // Subscribe to the indicator update event
            ParametrsChangeByUser += UTBotStc_ParametrsChangeByUser; ;

            // Subscribe to the candle finished event
            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;
            _tab.PositionOpeningSuccesEvent += _tab_PositionOpen;

            Description = "The Counter trend robot on Ema with ATR. " +
                "Buy: " +
                "1. The volume is above the VolumeValue. " +
                "2. Candle falling. " +
                "3. The price is below Ema. " +
                "4. The value of Atr is higher than the average value for a certain period (CandlesCountAtr) by MultAtr times. " +
                "Sell: " +
                "1. The volume is above the VolumeValue. " +
                "2. Candle growing. " +
                "3. The price is higher than Ema. " +
                "4. The value of Atr is higher than the average value for a certain period (CandlesCountAtr) by MultAtr times. " +
                "Exit from buy: Trailing stop is placed at the minimum for the period specified for the " +
                "trailing stop and is transferred (sliding) to new price lows, also for the specified period. " +
                "Exit from sell: Trailing stop is placed on the maximum for the period specified for " +
                "the trailing stop and is transferred (sliding) to a new price maximum, also for the specified period.";
        }

        private void UTBotStc_ParametrsChangeByUser()
        {
            ((IndicatorParameterInt)_Ema.Parameters[0]).ValueInt = PeriodEma.ValueInt;
            _Ema.Save();
            _Ema.Reload();
            ((IndicatorParameterInt)_ATR.Parameters[0]).ValueInt = AtrPeriod.ValueInt;
            _ATR.Save();
            _ATR.Reload();
        }

        // The name of the robot in OsEngine
        public override string GetNameStrategyType()
        {
            return "UTBotStc";
        }
        public override void ShowIndividualSettingsDialog()
        {

        }

        // Candle Finished Event
        private void _tab_CandleFinishedEvent(List<Candle> candles)
        {
            // If the robot is turned off, exit the event handler
            if (Regime.ValueString == "Off")
            {
                return;
            }

            // If there are not enough candles to build an indicator, we exit
            if (candles.Count < AtrPeriod.ValueInt ||
                candles.Count < PeriodEma.ValueInt ||
                _macd.DataSeries[0].Values == null)
            {
                return;
            }

            // The last value of the indicator
            _lastEma = _Ema.DataSeries[0].Last;
            _lastATR = _ATR.DataSeries[0].Last;
            _lastPrice = candles[candles.Count - 1].Close;
            _prevPrice = candles[candles.Count - 2].Close;
            _xATRTrailingStop = CalculateATRTrailingStop(_lastPrice, _prevPrice, _lastNloss, _xATRTrailingStop);

            _lastMacdDown = _macd.DataSeries[1].Values[_macd.DataSeries[1].Values.Count - 1];
            _lastMacdUp = _macd.DataSeries[2].Values[_macd.DataSeries[2].Values.Count - 1];

            if (_lastPrice > _xATRTrailingStop
                    && _lastEma > _xATRTrailingStop)
            {
                _isLongEnabled = true;
                _isShortEnabled = false;
            }
            if (_lastPrice < _xATRTrailingStop
                && _lastEma < _xATRTrailingStop)
            {
                _isLongEnabled = false;
                _isShortEnabled = true;
            }

            List<Position> openPositions = _tab.PositionsOpenAll;

            if (openPositions != null && openPositions.Count != 0)
            {
                for (int i = 0; i < openPositions.Count; i++)
                {
                    _tab_PositionOpen(openPositions[i]);
                    // LogicClosePosition(openPositions[i]);
                }
            }

            // If the position closing mode, then exit the method
            if (Regime.ValueString == "OnlyClosePosition")
            {
                return;
            }
            // If there are no positions, then go to the position opening method
            if (openPositions == null || openPositions.Count == 0)
            {
                LogicOpenPosition();
            }
        }

        private void _tab_PositionOpen(Position position)
        {

            decimal _slippage = Slippage.ValueDecimal * _tab.Security.PriceStep;
            var takeProfitPrice = position.EntryPrice + (position.EntryPrice - _xATRTrailingStop) * 1.5m;
            // var stopOrderPrice = position.MaxVolume > position.OpenVolume ? position.EntryPrice : _xATRTrailingStop;
            var stopOrderPrice = _xATRTrailingStop;

            if (position.Direction == Side.Buy)
            {
                var takeHalfProfitPrice = position.EntryPrice + (takeProfitPrice - position.EntryPrice) / 2;
                if ((position.CloseOrders == null || !position.CloseOrders.Any()) && _lastPrice >= takeHalfProfitPrice)
                {
                    _tab.CloseAtLimitUnsafe(position, takeHalfProfitPrice, position.OpenVolume / 2);
                    _tab.CloseAtStop(position, position.EntryPrice, position.EntryPrice + _slippage);
                }

                if (position.StopOrderPrice != 0)
                {
                    return;
                }
                _tab.CloseAtStop(position, stopOrderPrice, stopOrderPrice - _slippage);
                _tab.CloseAtProfit(
               position, takeProfitPrice,
               takeProfitPrice - _slippage);

                //if (position.CloseOrders != null && position.CloseOrders.Any())
                //    return;
                //_tab.CloseAtProfit(
                //position, takeProfitPrice,
                //takeProfitPrice - _slippage);
                //_tab.BuyAtStopMarket(GetVolume(), takeHalfProfitPrice, takeHalfProfitPrice - _slippage, StopActivateType.HigherOrEqual, 2, "Buy at stop market", PositionOpenerToStopLifeTimeType.NoLifeTime);
                //_tab.BuyAtLimit(GetVolume(), takeHalfProfitPrice + _slippage);
                //_tab.SellAtLimitToPositionUnsafe(position, takeProfitPrice, position.OpenVolume / 2);
                // _tab.SellAtLimitToPositionUnsafe(position, takeHalfProfitPrice, position.OpenVolume / 2);
                // _tab.CloseAtLimit(position, takeHalfProfitPrice, position.OpenVolume / 2);
                // _tab.CloseAtTrailingStop(position, takeHalfProfitPrice, takeHalfProfitPrice - _slippage);

            }
            else
            {
                var takeHalfProfitPrice = position.EntryPrice - (position.EntryPrice - takeProfitPrice) / 2;
                if ((position.CloseOrders == null || !position.CloseOrders.Any()) && _lastPrice <= takeHalfProfitPrice)
                {
                    _tab.CloseAtLimitUnsafe(position, takeHalfProfitPrice, position.OpenVolume / 2);
                    _tab.CloseAtStop(position, position.EntryPrice, position.EntryPrice + _slippage);
                }
                if (position.StopOrderPrice != 0)
                {
                    return;
                }


                _tab.CloseAtStop(position, stopOrderPrice, stopOrderPrice + _slippage);
                _tab.CloseAtProfit(
            position, takeProfitPrice,
            takeProfitPrice + _slippage);

                //if (position.CloseOrders != null && position.CloseOrders.Any())
                //    return;
                //var takeHalfProfitPrice = position.EntryPrice - (position.EntryPrice - takeProfitPrice) / 2;
                //    _tab.CloseAtProfit(
                //position, takeHalfProfitPrice,
                //takeHalfProfitPrice + _slippage);
                // _tab.SellAtStopMarket(GetVolume(), takeHalfProfitPrice, takeHalfProfitPrice + _slippage, StopActivateType.LowerOrEqyal, 2, "Sell at stop market", PositionOpenerToStopLifeTimeType.NoLifeTime);


                // _tab.CloseAtLimit(position, takeProfitPrice, position.OpenVolume / 2);
                //_tab.CloseAtLimit(position, takeHalfProfitPrice, position.OpenVolume / 2);
            }
        }

        private void LogMessage(string v)
        {
            if (StartProgram == StartProgram.IsOsTrader)
            {
                _tab.SetNewLogMessage(v, LogMessageType.Trade);
            }
        }

        // Opening logic
        private void LogicOpenPosition()
        {
            decimal _slippage = Slippage.ValueDecimal * _tab.Security.PriceStep;
            // Long
            if (Regime.ValueString != "OnlyShort") // If the mode is not only short, then we enter long
            {
                if (_isLongEnabled
                    && _lastMacdUp < _lastMacdDown
                    && _lastMacdUp < 0 && _lastMacdDown < 0)
                {
                    _tab.BuyAtLimit(GetVolume(), _tab.PriceBestAsk + _slippage);
                }
            }

            // Short
            if (Regime.ValueString != "OnlyLong") // If the mode is not only long, then we enter short
            {
                if (_isShortEnabled
                    && _lastMacdUp > _lastMacdDown
                    && _lastMacdDown > 0 && _lastMacdUp > 0)
                {
                    _tab.SellAtLimit(GetVolume(), _tab.PriceBestBid - _slippage);
                }
            }
        }

        // Logic close position
        private void LogicClosePosition(Position position)
        {
            if (position.State != PositionStateType.Open)
            {
                return;
            }

            decimal _slippage = Slippage.ValueDecimal * _tab.Security.PriceStep;

            if (position.Direction == Side.Buy) // If the direction of the position is purchase
            {
                if (_lastPrice < _xATRTrailingStop
                    && _lastMacdUp < _lastMacdDown
                    && _lastMacdDown > 0)
                    _tab.CloseAtTrailingStop(position, _lastPrice, _lastPrice - _slippage);
            }
            else // If the direction of the position is sale
            {
                if (_lastPrice > _xATRTrailingStop
                    && _lastMacdUp > _lastMacdDown
                    && _lastMacdUp < 0)
                    _tab.CloseAtTrailingStop(position, _lastPrice, _lastPrice + _slippage);
            }
        }

        // Method for calculating the volume of entry into a position
        private decimal GetVolume()
        {
            decimal volume = 0;

            if (VolumeRegime.ValueString == "Contract currency")
            {
                decimal contractPrice = _tab.PriceBestAsk;
                volume = VolumeOnPosition.ValueDecimal / contractPrice;
            }
            else if (VolumeRegime.ValueString == "Number of contracts")
            {
                volume = VolumeOnPosition.ValueDecimal;
            }

            // If the robot is running in the tester
            if (StartProgram == StartProgram.IsTester)
            {
                volume = Math.Round(volume, 6);
            }
            else
            {
                volume = Math.Round(volume, _tab.Security.DecimalsVolume);
            }
            return volume;
        }

        private decimal CalculateATRTrailingStop(decimal price, decimal prevPrice, decimal nLoss, decimal prevTrailingStop)
        {
            if (price > prevTrailingStop && prevPrice > prevTrailingStop)
            {
                return Math.Max(prevTrailingStop, price - nLoss);
            }
            else if (price < prevTrailingStop && prevPrice < prevTrailingStop)
            {
                return Math.Min(prevTrailingStop, price + nLoss);
            }
            else if (price > prevTrailingStop)
            {
                return price - nLoss;
            }
            else
            {
                return price + nLoss;
            }
        }
    }
}
