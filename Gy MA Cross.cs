using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GyMACross : Robot
    {
        // cBotタイマー
        [Parameter("cBot AUTO STOP", Group = "cBot AUTO STOP", DefaultValue = false)]
        public Boolean cbotautostop { get; set; }
        // デフォルト 480 は 1h で 20日分
        [Parameter("cBot OFF Timer (candles)", Group = "cBot AUTO STOP", DefaultValue = 480, MinValue = 5)]
        public int cbottimeout { get; set; }
        public int cbottimer = 0;

        // Trade
        [Parameter("Buy", Group = "Trade", DefaultValue = true)]
        public Boolean actbuy { get; set; }
        [Parameter("Sell", Group = "Trade", DefaultValue = true)]
        public Boolean actsell { get; set; }
        // 1h 200MA をベースにしているのでボラ急騰への備えは不要（むしろ機会損失）と判断
        // 金曜日は取引しない：デフォルトOFF
        [Parameter("no Trade on Friday", Group = "Trade", DefaultValue = false)]
        public Boolean fridayoff { get; set; }
        // 月曜朝のギャップ避け Sunday UTC 22:00：デフォルトOFF
        [Parameter("Monday start at AM7:00(UTC+9)", Group = "Trade", DefaultValue = false)]
        public Boolean mondaymorning { get; set; }
        public Boolean activetime = true;

        // Order
        [Parameter("Quantity (Lots)", Group = "Volume", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double Quantity { get; set; }
        [Parameter("Stop Loss (pips)", Group = "Protection", DefaultValue = 100, MinValue = 1)]
        public int StopLossInPips { get; set; }
        [Parameter("Take Profit (pips)", Group = "Protection", DefaultValue = 5000, MinValue = 1)]
        public int TakeProfitInPips { get; set; }

        // TrailingStopLoss
        [Parameter("CLOSE処理にTSLを使用", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = true)]
        public Boolean bCloseTSL { get; set; }
        [Parameter("Trailing Stop Loss (pips)", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = 30, MinValue = 1)]
        public int TrailStopLossInPips { get; set; }

        // Base MovingAverage
        // 長期線。基準に使用
        [Parameter("Source", Group = "Base MovingAverage")]
        public DataSeries SrcMAb1 { get; set; }
        [Parameter("Periods", Group = "Base MovingAverage", DefaultValue = 200)]
        public int PrdMAb1 { get; set; }
        [Parameter("MAType", Group = "Base MovingAverage", DefaultValue = 0)]
        private MovingAverageType TypeMAb1 { get; set; }
        private MovingAverage base1;

        // Sub MovingAverage
        // フィルターに使用
        [Parameter("Source", Group = "Sub MovingAverage")]
        public DataSeries SrcMAb2 { get; set; }
        [Parameter("Periods", Group = "Sub MovingAverage", DefaultValue = 75)]
        public int PrdMAb2 { get; set; }
        [Parameter("MAType", Group = "Sub MovingAverage", DefaultValue=0)]
        private MovingAverageType TypeMAb2 { get; set; }
        private MovingAverage base2;

        // Trigger MovingAverage
        // 短期線。トリガーに使用。長期線を上抜けで買い、下抜けで売り
        [Parameter("Source", Group = "Trigger MovingAverage")]
        public DataSeries SrcMAt { get; set; }
        [Parameter("Periods", Group = "Trigger MovingAverage", DefaultValue = 25)]
        public int PrdMAt { get; set; }
        [Parameter("MAType", Group = "Trigger MovingAverage", DefaultValue = 0)]
        private MovingAverageType TypeMAt { get; set; }
        private MovingAverage main;

        // 値動きを捕捉し状態をステータス管理
        // 0：初期値
        // 1：STATE=0 で2σトップへ到達、3：STATE=1 で2σトップを下抜け、5：STATE=3 で1σトップまで到達、7：STATE=5 でMAINへ到達
        // 2：STATE=0 で2σボトムへ到達、4：STATE=2 で2σボトムを上抜け、6：STATE=4 で1σボトムまで到達、8：STATE=6 でMAINへ到達
        public String status = "FLAT";

        // 各ステータス間のタイムアウト（足数）
        // 要チューニング
        //[Parameter("STATE: 1 or 2", Group = "TimeOut (Candles)", DefaultValue = 72)]
        //public int timeout1 { get; set; }
        //[Parameter("STATE: 3 or 4", Group = "TimeOut (Candles)", DefaultValue = 72)]
        //public int timeout2 { get; set; }
        //[Parameter("STATE: 5 or 6", Group = "TimeOut (Candles)", DefaultValue = 72)]
        //public int timeout3 { get; set; }

        // タイムアウト制御用のカウンタ
        // ステータス変化でリセット
        public int cnt = 0;

        // base1タッチでリセット
        public int cnt2 = 0;

        protected override void OnStart()
        {
            Print("START GyMACross: {0}", Server.Time.ToLocalTime());
            base1 = Indicators.MovingAverage(SrcMAb1, PrdMAb1,TypeMAb1);
            base2 = Indicators.MovingAverage(SrcMAb2, PrdMAb2, TypeMAb2);
            main = Indicators.MovingAverage(SrcMAt, PrdMAt,TypeMAt);
        }

        protected override void OnStop()
        {
            Print("STOP GyMACross: {0}", Server.Time.ToLocalTime());
        }

        protected override void OnError(Error error)
        {
            Print(error.Code);
            if (error.Code == ErrorCode.NoMoney)
                Stop();
        }

        private void Close(TradeType tradeType)
        {
            foreach (var position in Positions.FindAll("GyMACross", SymbolName, tradeType))
            {
                ClosePosition(position);
                Print("[CLOSE ] [ {0} ] STATUS : {1}", tradeType, status);
            }
        }

        private void Open(TradeType tradeType)
        {
            // 同じポジは１つだけ
            var position = Positions.Find("GyMACross", SymbolName, tradeType);
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(Quantity);
            if (position == null)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "GyMACross", StopLossInPips, TakeProfitInPips);
                Print("[ OPEN ] [ {0} ] STATUS : {1}", tradeType, status);
            }
        }

        private void SetTSL(TradeType tradeType)
        {
            double? newstop;
            foreach (var position in Positions.FindAll("GyMACross", SymbolName, tradeType))
            {
                if (tradeType == TradeType.Buy)
                    newstop = Symbol.Bid - Symbol.PipSize * TrailStopLossInPips;
                else
                    newstop = Symbol.Ask + Symbol.PipSize * TrailStopLossInPips;

                ModifyPosition(position, newstop, position.TakeProfit, true);
                Print("[MODIFY] [ {0} ] SET TrailingStopLoss : {1}", tradeType, newstop);
            }
        }

        protected override void OnBar()
        {
            // 金曜日OFF設定（月曜日は朝一ギャップを除外したいのでAM7:00から(UTC+9)）
            if ((!(fridayoff == true && Server.Time.DayOfWeek >= DayOfWeek.Friday)) && !(mondaymorning == true && (Server.Time.DayOfWeek == DayOfWeek.Sunday && Server.Time.Hour < 22)))
                activetime = true;
            else
                activetime = false;

            var base1last = base1.Result.Last(0);
            var avr0 = main.Result.Last(0);
            var avr1 = main.Result.Last(1);

            // cBot AUTO STOP
            if (cbotautostop == true)
            {
                if (cbottimer > cbottimeout)
                {
                    Print("cBot AUTO STOP [ cBot Timer : {0} / {1} ]", cbottimer, cbottimeout);
                    Close(TradeType.Buy);
                    Close(TradeType.Sell);
                    Stop();
                }
                else
                    cbottimer = cbottimer + 1;
            }

           // ステータス変更後カウンタ
           cnt = cnt + 1;

            // 基準線の鮮度
            if ((avr1 <= base1.Result.Last(1)) && (avr0 > base1.Result.Last(1)))
                cnt2 = 0;  // base1と交差したらリセット
            else if ((avr1 >= base1.Result.Last(1)) && (avr0 < base1.Result.Last(1)))
                cnt2 = 0;  // base1と交差したらリセット
            else
                cnt2 = cnt2 + 1;

            switch (status)
            {
                case "FLAT":
                    if (avr1 <= base1last && avr0 > base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)上抜け", avr0, status, PrdMAb1);
                        Open(TradeType.Buy);
                        status = "LONG";
                    }
                    else if (avr1 >= base1last && avr0 < base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)下抜け", avr0, status, PrdMAb1);
                        Open(TradeType.Sell);
                        status = "SHORT";
                    }
                    break;
  
                case "LONG":
                    if (avr1 >= base1last && avr0 < base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)下抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                        Close(TradeType.Buy);
                        Open(TradeType.Sell);
                        status = "SHORT";
                    }
                    else if (avr0 < base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)下抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                        Close(TradeType.Buy);
                        status = "FLAT";
                    }
                    else if (avr1 <= base1last && avr0 > base1last)
                    {
                        Print("<要デバッグ> {0} [{1}] 長期線({2}MA)上抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                    }
                    break;

                case "SHORT":
                    if (avr1 <= base1last && avr0 > base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)上抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                        Close(TradeType.Sell);
                        Open(TradeType.Buy);
                        status = "LONG";
                    }
                    else if (avr0 > base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)上抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                        Close(TradeType.Sell);
                        status = "FLAT";
                    }
                    else if(avr1 >= base1last && avr0 < base1last)
                    {
                        Print("<要デバッグ> {0} [{1}] 長期線({2}MA)下抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                    }
                    break;

                case "LONG-SHORT":
                    if (avr1 <= base1last && avr0 > base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)上抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                        Close(TradeType.Sell);
                        status = "LONG";
                    }
                    else if (avr1 >= base1last && avr0 < base1last)
                    {
                        Print("{0} [{1}] 長期線({2}MA)下抜け  cnt2; {3}", avr0, status, PrdMAb1, cnt2);
                        Close(TradeType.Buy);
                        status = "SHORT";
                    }
                    break;
            }
        }
    }
}


