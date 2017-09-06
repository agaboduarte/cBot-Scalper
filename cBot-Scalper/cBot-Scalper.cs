﻿//+------------------------------------------------------------------+
//|                                                  Smart Grid      |
//|                                      Copyright 2014, MD SAIF     |
//|                                   http://www.facebook.com/cls.fx |
//+------------------------------------------------------------------+
//-Grid trader cBot based on Bar-Time & Trend. For range market & 15 minute TimeFrame is best.

using System;
using System.Linq;
using cAlgo.API;

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class cBotScalper : Robot
    {
        [Parameter("Buy Enabled", DefaultValue = true)]
        public bool BuyEnabled { get; set; }

        [Parameter("Sell Enabled", DefaultValue = true)]
        public bool SellEnabled { get; set; }

        [Parameter("Pip Step", DefaultValue = 1, MinValue = 1)]
        public int PipStep { get; set; }

        [Parameter("First Volume", DefaultValue = 1000, MinValue = 1000, Step = 1000)]
        public int FirstVolume { get; set; }

        [Parameter("Volume Exponent", DefaultValue = 2.8, MinValue = 0.1, MaxValue = 5.0)]
        public double VolumeExponent { get; set; }

        [Parameter("Max Spread", DefaultValue = 3)]
        public double MaxSpread { get; set; }

        [Parameter("Stop Loss Pips", DefaultValue = 75, MinValue = 1, Step = 1)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit Average", DefaultValue = 21, MinValue = 1)]
        public int TakeProfitAverage { get; set; }

        [Parameter("Close On Stop", DefaultValue = false)]
        public bool CloseOnStop { get; set; }

        public bool HasRick
        {
            get { return UseRiskStrategy || FridayCloseOperations; }
        }

        public bool UseRiskStrategy
        {
            get { return Time.DayOfWeek == DayOfWeek.Friday && Time.ToUniversalTime().TimeOfDay >= fridayUseRiskStrategy; }
        }

        public bool FridayCloseOperations
        {
            get { return Time.DayOfWeek == DayOfWeek.Friday && Time.ToUniversalTime().TimeOfDay >= fridayCloseOperations; }
        }

        private string Label = "cls";
        private DateTime buyLastOpenTime;
        private DateTime sellLastOpenTime;
        private double spreedValue;
        private bool stopped = false;
        private double operationsBalance = 0;
        private DateTime maxProfitDate;
        private double maxProfit = 0;
        private DateTime maxLossDate;
        private double maxLoss = 0;
        private TimeSpan fridayUseRiskStrategy = new TimeSpan(0, 0, 0);
        private TimeSpan fridayCloseOperations = new TimeSpan(19, 0, 0);

        protected override void OnTick()
        {
            try
            {
                spreedValue = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;

                GetOperationsBalance();

                if (TotalOpenPositions(TradeType.Buy) > 0 && !UseRiskStrategy)
                    SetBuyTakeProfit(AveragePrice(TradeType.Buy), TakeProfitAverage);

                if (TotalOpenPositions(TradeType.Sell) > 0 && !UseRiskStrategy)
                    SetSellTakeProfit(AveragePrice(TradeType.Sell), TakeProfitAverage);

                if (spreedValue <= MaxSpread && !HasRick && !stopped)
                    OpenPosition();

                if (UseRiskStrategy)
                    RiskModifyTakeProfitAndStopLoss();

                if (TotalOpenPositions(TradeType.Sell) > 0 || TotalOpenPositions(TradeType.Buy) > 0)
                    PreserveMaxProfitAndLoss();

                if (FridayCloseOperations)
                    CloseAllPositions();
            } catch (Exception e)
            {
                Print(e);

                throw;
            }
        }

        protected override void OnError(Error error)
        {
            if (error.Code == ErrorCode.NoMoney)
            {
                stopped = true;

                Print("Openning stopped because: not enough money");
            }

            Print("Error: ", error);
        }

        protected override void OnStop()
        {
            PreserveMaxProfitAndLoss();

            if (CloseOnStop)
                CloseAllPositions();

            Print("Max Profit Date: ", maxProfitDate);
            Print("Max Profit: ", maxProfit);
            Print("Max Loss Date: ", maxLossDate);
            Print("Max Loss: ", maxLoss);
        }

        private double GetOperationsBalance()
        {
            var balance = 0.0;

            foreach (var position in Positions.FindAll(Label, Symbol))
                balance += position.GrossProfit;

            operationsBalance = balance;

            return balance;
        }

        private void PreserveMaxProfitAndLoss()
        {
            var balance = GetOperationsBalance();

            if (balance >= maxProfit)
            {
                maxProfit = balance;
                maxProfitDate = Time;
            }

            if (balance <= maxLoss)
            {
                maxLoss = balance;
                maxLossDate = Time;
            }
        }

        private void OpenPosition()
        {
            if (BuyEnabled && TotalOpenPositions(TradeType.Buy) == 0 && MarketSeries.Close.Last(1) > MarketSeries.Close.Last(2))
            {
                var success = SendOrder(TradeType.Buy, FirstVolume);

                if (success)
                    buyLastOpenTime = MarketSeries.OpenTime.Last(0);
                else
                    Print("First BUY openning error at: ", Symbol.Ask, "Error Type: ", LastResult.Error);
            }

            if (SellEnabled && TotalOpenPositions(TradeType.Sell) == 0 && MarketSeries.Close.Last(2) > MarketSeries.Close.Last(1))
            {
                var success = SendOrder(TradeType.Sell, FirstVolume);

                if (success)
                    sellLastOpenTime = MarketSeries.OpenTime.Last(0);
                else
                    Print("First SELL openning error at: ", Symbol.Bid, "Error Type: ", LastResult.Error);
            }

            MakeAveragePrice();
        }

        private void CloseAllPositions()
        {
            foreach (var position in Positions.FindAll(Label, Symbol))
                ClosePosition(position);
        }

        private void RiskModifyTakeProfitAndStopLoss()
        {
            foreach (var position in Positions.FindAll(Label, Symbol))
            {
                var stopLoss = Math.Round(GetAbsoluteStopLoss(position, TakeProfitAverage), Symbol.Digits);
                var takeProfit = Math.Round(GetAbsoluteTakeProfit(position, TakeProfitAverage), Symbol.Digits);

                if (position.Pips <= 0)
                {
                    var pips = Math.Abs(position.Pips) + TakeProfitAverage;

                    stopLoss = Math.Round(position.TradeType == TradeType.Buy ? Symbol.Bid - Symbol.PipSize * pips : Symbol.Ask + Symbol.PipSize * pips, Symbol.Digits);
                    takeProfit = Math.Round(position.TradeType == TradeType.Buy ? Symbol.Ask + Symbol.PipSize * pips : Symbol.Bid - Symbol.PipSize * pips, Symbol.Digits);
                }

                if (Math.Round(position.StopLoss ?? 0, Symbol.Digits) != stopLoss || Math.Round(position.TakeProfit ?? 0, Symbol.Digits) != takeProfit)
                    ModifyPosition(position, stopLoss, takeProfit);
            }
        }

        private void MakeAveragePrice()
        {
            if (TotalOpenPositions(TradeType.Buy) > 0)
            {
                if (Math.Round(Symbol.Ask, Symbol.Digits) < Math.Round(GetMinEntryPrice(TradeType.Buy) - PipStep * Symbol.PipSize, Symbol.Digits) && buyLastOpenTime != MarketSeries.OpenTime.Last(0))
                {
                    long volume = CalculateVolume(TradeType.Buy);

                    var success = SendOrder(TradeType.Buy, volume);

                    if (success)
                        buyLastOpenTime = MarketSeries.OpenTime.Last(0);
                    else
                        Print("Next BUY openning error at: ", Symbol.Ask, "Error Type: ", LastResult.Error);
                }
            }

            if (TotalOpenPositions(TradeType.Sell) > 0)
            {
                if (Math.Round(Symbol.Bid, Symbol.Digits) > Math.Round(GetMaxEntryPrice(TradeType.Sell) + PipStep * Symbol.PipSize, Symbol.Digits) && sellLastOpenTime != MarketSeries.OpenTime.Last(0))
                {
                    long volume = CalculateVolume(TradeType.Sell);

                    var success = SendOrder(TradeType.Sell, volume);

                    if (success)
                        sellLastOpenTime = MarketSeries.OpenTime.Last(0);
                    else
                        Print("Next SELL openning error at: ", Symbol.Bid, "Error Type: ", LastResult.Error);
                }
            }
        }

        private bool SendOrder(TradeType tradeType, long volume)
        {
            if (volume <= 0)
            {
                Print("Volume calculation error: Calculated Volume is: ", volume);

                return false;
            }

            TradeResult result = ExecuteMarketOrder(tradeType, Symbol, volume, Label, StopLossPips, 0, 0, "smart_grid");

            if (!result.IsSuccessful)
            {
                Print("Openning Error: ", result.Error);

                return false;
            }

            return true;
        }

        private void SetBuyTakeProfit(double ai_4, int ad_8)
        {
            foreach (var position in Positions.FindAll(Label, Symbol, TradeType.Buy))
            {
                double? takeProfit = Math.Round(ai_4 + ad_8 * Symbol.PipSize, Symbol.Digits);

                if (position.TakeProfit != takeProfit)
                    ModifyPosition(position, position.StopLoss, takeProfit);
            }
        }

        private void SetSellTakeProfit(double averagePrice, int takeProfitAverage)
        {
            foreach (var position in Positions.FindAll(Label, Symbol, TradeType.Sell))
            {
                double? takeProfit = Math.Round(averagePrice - takeProfitAverage * Symbol.PipSize, Symbol.Digits);

                if (position.TakeProfit != takeProfit)
                    ModifyPosition(position, position.StopLoss, takeProfit);
            }
        }

        private int TotalOpenPositions(TradeType tradeType)
        {
            return Positions.FindAll(Label, Symbol, tradeType).Length;
        }

        private double AveragePrice(TradeType tradeType)
        {
            double totalPositionValue = 0;
            long totalVolume = 0;

            foreach (var position in Positions.FindAll(Label, Symbol, tradeType))
            {
                totalPositionValue += position.EntryPrice * position.Volume;
                totalVolume += position.Volume;
            }

            if (totalPositionValue > 0 && totalVolume > 0)
                return Math.Round(totalPositionValue / totalVolume, Symbol.Digits);

            return 0;
        }

        private double GetMinEntryPrice(TradeType tradeType)
        {
            var positions = Positions.FindAll(Label, Symbol, tradeType);

            if (positions.Length == 0)
                return 0;

            return positions.Min(i => i.EntryPrice);
        }

        private double GetMaxEntryPrice(TradeType tradeType)
        {
            var positions = Positions.FindAll(Label, Symbol, tradeType);

            if (positions.Length == 0)
                return 0;

            return positions.Max(i => i.EntryPrice);
        }

        private double GetFirstEntryPrice(TradeType tradeType)
        {
            var lastPosition = Positions.FindAll(Label, Symbol, tradeType).OrderBy(i => i.Id).FirstOrDefault();

            if (lastPosition == null)
                return 0;

            return lastPosition.EntryPrice;
        }

        private long GetFirstVolume(TradeType tradeType)
        {
            var lastPosition = Positions.FindAll(Label, Symbol, tradeType).OrderBy(i => i.Id).FirstOrDefault();

            if (lastPosition == null)
                return 0;

            return lastPosition.Volume;
        }

        private long PositionVolume(TradeType tradeType)
        {
            return Positions.FindAll(Label, Symbol, tradeType).Sum(i => i.Volume);
        }

        private int GetTotalOperationOnPrice(TradeType tradeType, double price)
        {
            var positions = Positions.FindAll(Label, Symbol, tradeType);

            if (tradeType == TradeType.Buy)
                return positions.Count(i => Math.Round(i.EntryPrice, Symbol.Digits) <= Math.Round(price, Symbol.Digits));

            return positions.Count(i => Math.Round(i.EntryPrice, Symbol.Digits) >= Math.Round(price, Symbol.Digits));
        }

        private long CalculateVolume(TradeType tradeType)
        {
            double firstEntryPrice = GetFirstEntryPrice(tradeType);
            long firstVolume = GetFirstVolume(tradeType);
            int totalOperation = GetTotalOperationOnPrice(tradeType, firstEntryPrice);

            return Symbol.NormalizeVolume(firstVolume * Math.Pow(VolumeExponent, totalOperation <= 0 ? 1 : totalOperation));
        }

        private double GetAbsoluteStopLoss(Position position, double stopLossPips)
        {
            return position.TradeType == TradeType.Buy ? position.EntryPrice - Symbol.PipSize * stopLossPips : position.EntryPrice + Symbol.PipSize * stopLossPips;
        }

        private double GetAbsoluteTakeProfit(Position position, double takeProfitPips)
        {
            return position.TradeType == TradeType.Buy ? position.EntryPrice + Symbol.PipSize * takeProfitPips : position.EntryPrice - Symbol.PipSize * takeProfitPips;
        }
    }
}
