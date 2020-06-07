using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GyBolliBanBreak : Robot
    {
        // cBotタイマー
        [Parameter("cBot AUTO STOP", Group = "cBot AUTO STOP", DefaultValue = false)]
        public Boolean CbotAutoStop { get; set; }
        // デフォルト 1440 は5m で月曜から金曜まで5日分
        [Parameter("cBot OFF Timer (candles)", Group = "cBot AUTO STOP", DefaultValue = 1440, MinValue = 5)]
        public int CbotTimeOut { get; set; }
        public int CbotTimer = 0;

        // Trade
        [Parameter("買いを実施", Group = "Trade", DefaultValue = true)]
        public Boolean ActBuy { get; set; }
        [Parameter("売りを実施", Group = "Trade", DefaultValue = true)]
        public Boolean ActSell { get; set; }
        // 第一金曜日は取引しない
        [Parameter("雇用統計の日はOFF", Group = "Trade", DefaultValue = true)]
        public Boolean FirstFridayOff { get; set; }
        // 月曜朝のギャップ避け Sunday UTC 24:00
        [Parameter("週初はAM9:00開始", Group = "Trade", DefaultValue = true)]
        public Boolean MondayMorning { get; set; }

        // Order
        [Parameter("Quantity (Lots)", Group = "Volume", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double Quantity { get; set; }
        [Parameter("Stop Loss (pips)", Group = "Protection", DefaultValue = 70, MinValue = 1)]
        public int StopLossInPips { get; set; }
        [Parameter("Take Profit (pips)", Group = "Protection", DefaultValue = 500, MinValue = 1)]
        public int TakeProfitInPips { get; set; }

        // TrailingStopLoss
        [Parameter("CLOSE処理にTSLを使用", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = true)]
        public Boolean CloseTSL { get; set; }
        [Parameter("Trailing Stop Loss (pips)", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = 30, MinValue = 1)]
        public int TrailStopLossInPips { get; set; }

        // BollingerBands
        private BollingerBands BolliBan1;
        private BollingerBands BolliBan2;
        [Parameter("Source", Group = "BollingerBands")]
        public DataSeries SourceBB { get; set; }
        // 1h -> 5m 調整 　参考）14*12=168、 25*12=300
        // 分析対象として1hを使用するがエントリーの精度を上げるため5mで使用する
        [Parameter("Periods", Group = "BollingerBands", DefaultValue = 300)]
        public int PeriodBB { get; set; }
        [Parameter("Deviations", Group = "BollingerBands", DefaultValue = 1)]
        public double DeviateBB1 { get; set; }
        [Parameter("撤退基準に使用", Group = "BollingerBands", DefaultValue = false)]
        public Boolean CloseBB1 { get; set; }
        [Parameter("Deviations", Group = "BollingerBands", DefaultValue = 2)]
        public double DeviateBB2 { get; set; }
        [Parameter("MAType", Group = "BollingerBands", DefaultValue = 1)]
        public MovingAverageType MaTypeBB { get; set; }

        // Signal MovingAverage
        // シグナルに使用。現在値でなくMAにして調整の幅を持たせる
        private MovingAverage SignalMA;
        [Parameter("Source", Group = "Signal MovingAverage")]
        public DataSeries SourceSignalMA { get; set; }
        [Parameter("Periods", Group = "Signal MovingAverage", DefaultValue = 3)]
        public int PeriodSignalMA { get; set; }
        [Parameter("MAType", Group = "Signal MovingAverage", DefaultValue = 1)]
        public MovingAverageType MaTypeSignal { get; set; }

        // DirectionalMovementSystem 
        // フィルターに使用。トレンド判定に使用
        private DirectionalMovementSystem FilterADX;
        private double diplus;
        private double diminus;
        private double diadx;
        [Parameter("ADX Period", DefaultValue = 14)]
        public int PeriodADX { get; set; }

        // 値動きの状態 （奇数はロング、偶数はショート用）
        public enum States
        {
            Initial = 0,                // 0：初期値
            Top2_Above = 1,             // 1：STATE=0 で2σトップ上抜け
            Bottom2_Below = 2,          // 2：STATE=0 で2σボトム下抜け
            Top2_Below = 3,             // 3：STATE=1 で2σトップを下抜け
            Bottom2_Above = 4,          // 4：STATE=2 で2σボトムを上抜け
            Top1_Below = 5,             // 5：STATE=3 で1σトップを下抜け
            Bottom1_Above = 6,          // 6：STATE=4 で1σボトムを上抜け
            Top1_Above = 7,             // 7：STATE=5 で1σトップを上抜け
            Bottom1_Below = 8           // 8：STATE=6 で1σボトムを下抜け
        }
        public States curstate;
        
        // 各ステータス間のタイムアウト（足数）
        // 要チューニング
        [Parameter("STATE: 1 or 2", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int TimeOut1 { get; set; }
        [Parameter("STATE: 3 or 4", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int TimeOut2 { get; set; }
        [Parameter("STATE: 5 or 6", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int TimeOut3 { get; set; }

        // タイムアウト制御用のカウンタ
        // ステータス変化でリセット
        public int StateCount = 0;

        // ポジションの状態（Buy）
        public enum HasLong
        {
            Initial = 0,          // 0：ポジションなし
            Open = 1,             // 1：ロングOpen。MAINタッチなら即CLOSE
            ToClose = 2,          // 2：Open以降に2σ上抜け、利を伸ばす。１σタッチ他でCLOSE
            Closed = 3,           // 3：PositionsOnClosedでInitialに戻す目印。TSLやSLでのCLOSEに対応するため
        }
        public HasLong longstate;

        // ポジションの状態（Sell）
        public enum HasShort
        {
            Initial = 0,          // 0：ポジションなし
            Open = 1,             // 1：ショートOpen。MAINタッチなら即CLOSE
            ToClose = 2,          // 2：Open以降に2σ下抜け、利を伸ばす。１σタッチ他でCLOSE
            Closed = 3,           // 3：PositionsOnClosedでInitialに戻す目印。TSLやSLでのCLOSEに対応するため
        }
        public HasShort shortstate;

        protected override void OnStart()
        {
            Print("START GyBolliBanBreak: {0}", Server.Time.ToLocalTime());
            BolliBan1 = Indicators.BollingerBands(SourceBB, PeriodBB, DeviateBB1, MaTypeBB);
            BolliBan2 = Indicators.BollingerBands(SourceBB, PeriodBB, DeviateBB2, MaTypeBB);
            SignalMA = Indicators.SimpleMovingAverage(SourceSignalMA, PeriodSignalMA);
            FilterADX = Indicators.DirectionalMovementSystem(PeriodADX);
            curstate = States.Initial;
            longstate = HasLong.Initial;
            shortstate = HasShort.Initial;
        }

        protected override void OnStop()
        {
            Print("STOP GyBolliBanBreak: {0}", Server.Time.ToLocalTime());
        }

        protected override void OnError(Error error)
        {
            Print(error.Code);
            if (error.Code == ErrorCode.NoMoney)
                Stop();
        }

        private void Close(TradeType tradeType)
        {
            foreach (var position in Positions.FindAll("GyBolliBanBreak", SymbolName, tradeType))
            {
                ClosePosition(position);
                Print("[CLOSE ] [ {0} ] STATUS : {1}, Count : {2}", tradeType, curstate, StateCount);
            }
        }

        private void PositionsOnClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            var reas = args.Reason;
            Print("Position closed with {0} profit.    Reason : {1}", pos.GrossProfit, reas);
            // ポジションのステータスリセット
            if (longstate == HasLong.Closed)
                longstate = HasLong.Initial;
            if (shortstate == HasShort.Closed)
                shortstate = HasShort.Initial;
        }

        private void Open(TradeType tradeType)
        {
            var position = Positions.Find("GyBolliBanBreak", SymbolName, tradeType);
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(Quantity);
            if (position == null)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "GyBolliBanBreak", StopLossInPips, TakeProfitInPips);
                Print("[ OPEN ] [ {0} ] STATUS : {1}, Count : {2}", tradeType, curstate, StateCount);
            }
        }

        private void SetTSL(TradeType tradeType)
        {
            double? newstop;
            foreach (var position in Positions.FindAll("GyBolliBanBreak", SymbolName, tradeType))
            {
                if (tradeType == TradeType.Buy)
                    newstop = Symbol.Bid - Symbol.PipSize * TrailStopLossInPips;
                else
                    newstop = Symbol.Ask + Symbol.PipSize * TrailStopLossInPips;

                ModifyPosition(position, newstop, position.TakeProfit, true);
                Print("[MODIFY] [ {0} ] SET TrailingStopLoss : {1}", tradeType, newstop);
            }
        }

        private Boolean CheckOpen()
        { // 雇用統計対策　第一、第二金曜日OFF設定　
          //  および　週初開始時刻設定（月曜日は朝一ギャップを除外したいのでAM9:00から(UTC+9)
            if (!(FirstFridayOff == true && Server.Time.Day < 9 && Server.Time.DayOfWeek == DayOfWeek.Friday)
                && !(MondayMorning == true && Server.Time.DayOfWeek == DayOfWeek.Sunday && Server.Time.Hour < 24))
                return true;
            else
                return false;
        }

        protected override void OnBar()
        {
            var top1 = BolliBan1.Top.Last(0);
            var bottom1 = BolliBan1.Bottom.Last(0);
            var main = BolliBan1.Main.LastValue;
            var top2 = BolliBan2.Top.Last(0);
            var bottom2 = BolliBan2.Bottom.Last(0);
            var avr0 = SignalMA.Result.Last(0);
            var avr1 = SignalMA.Result.Last(1);
            diplus = FilterADX.DIPlus.LastValue;
            diminus = FilterADX.DIMinus.LastValue;
            diadx = FilterADX.ADX.LastValue;

            // cBot AUTO STOP
            if (CbotAutoStop == true)
            {
                if (CbotTimer > CbotTimeOut)
                {
                    Print("cBot AUTO STOP [ cBot Timer : {0} / {1} ]", CbotTimer, CbotTimeOut);
                    Close(TradeType.Buy);
                    Close(TradeType.Sell);
                    Stop();
                }
                else
                    CbotTimer = CbotTimer + 1;
            }

            // ステータス変更後カウンタ
            StateCount = StateCount + 1;
            
            // ステータス変化とトレード
            switch (curstate)
            {
                case States.Initial:

                    if (ActSell == true && avr1 >= bottom2 && avr0 < bottom2)
                    {
                        Print("{0}  2σボトム下抜け         STATE: {1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (CheckOpen())
                        {
                            curstate = States.Bottom2_Below;
                        }
                        StateCount = 0;
                    }
                    else if (ActBuy == true && avr1 <= top2 && avr0 > top2)
                    {
                        Print("{0}  2σトップ上抜け         STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (CheckOpen())
                        {
                            curstate = States.Top2_Above;
                        }
                        StateCount = 0;
                    }
                    break;

                case States.Top2_Above:
                    
                    if (avr0 < top2)
                    {
                        Print("{0}  2σトップ下抜け          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Top2_Below;
                        StateCount = 0;
                    }
                    break;

                case States.Bottom2_Below:
                    
                    if (avr0 > bottom2)
                    {
                        Print("{0}  2σボトム上抜け          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Bottom2_Above;
                        StateCount = 0;
                    }
                    break;

                case States.Top2_Below:
                    
                    if (avr0 < top1)
                    {
                        Print("{0}  トップ1σ下抜け          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (longstate == HasLong.ToClose)
                        {
                            longstate = HasLong.Closed; // Closeの前に変更
                            if (CloseTSL == true)
                            {
                                SetTSL(TradeType.Buy);  //TrailingSLで利を伸ばす
                                curstate = States.Top1_Below;

                                longstate = HasLong.Initial;
                            }
                            else
                            {
                                Close(TradeType.Buy);   //CLOSE
                                curstate = States.Initial;

                                longstate = HasLong.Initial;
                            }
                        }
                        else
                            curstate = States.Top1_Below;
                        StateCount = 0;
                    }
                    else if (avr0 > top2)
                    { 
                        Print("{0}  トップ2σ再度上抜け      STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Top2_Above;  //ステータス復帰
                        StateCount = 0;
                    }
                    break;

                case States.Bottom2_Above:
                    
                    if (avr0 > bottom1)
                    {
                        Print("{0}  ボトム1σ上抜け          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (shortstate == HasShort.ToClose)
                        {
                            shortstate = HasShort.Closed; // Closeの前に変更
                            if (CloseTSL == true)
                            {
                                SetTSL(TradeType.Sell);  //TrailingSLで利を伸ばす
                                curstate = States.Bottom1_Above;

                                shortstate = HasShort.Initial;
                            }
                            else
                            {
                                Close(TradeType.Sell);   //CLOSE
                                curstate = States.Initial;

                                shortstate = HasShort.Initial;
                            }
                        }
                        else
                            curstate = States.Bottom1_Above;
                        StateCount = 0;
                    }
                    else if (avr0 < bottom2)
                    {  
                        Print("{0}  ボトム2σ再度下抜け      STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Bottom2_Below;  //ステータス復帰
                        StateCount = 0;
                    }
                    break;

                case States.Top1_Below:
                    
                    if (avr0 <= main)
                    { 
                        Print("{0}  トップ1σ→MAIN         STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (longstate != HasLong.Initial)
                        {
                            longstate = HasLong.Closed;
                            Close(TradeType.Buy);   //CLOSE

                            longstate = HasLong.Initial;
                        }
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (avr0 > top1)
                    {  // ロング：BandWalk
                        Print("{0}  トップ1σ再度上抜け     STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Top1_Above;
                        if (longstate== HasLong.Initial)
                        {
                            Open(TradeType.Buy);  //ロング  エントリー条件厳格化（1σ戻り待ち）
                            longstate = HasLong.Open;
                        }
                        StateCount = 0;
                    }
                    break;

                case States.Bottom1_Above:
                    
                    if (avr0 >= main)
                    {  
                        Print("{0}  ボトム1σ→MAIN          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (shortstate != HasShort.Initial)
                        {
                            shortstate = HasShort.Closed;
                            Close(TradeType.Sell); // CLOSE

                            shortstate = HasShort.Initial;
                        }
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (avr0 < bottom1)
                    {  // ショート：BandWalk
                        Print("{0}  ボトム1σ再度下抜け      STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Bottom1_Below;
                        if (shortstate == HasShort.Initial)
                        {
                            Open(TradeType.Sell);  //ショート  エントリー条件厳格化（1σ戻り待ち）
                            shortstate = HasShort.Open;
                        }
                        StateCount = 0;
                    }
                    break;

                case States.Top1_Above:

                    if (avr0 <= main)
                    {  // ロング：CLOSE
                        Print("{0}  トップ1σ→MAIN          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (longstate != HasLong.Initial)
                        {
                            longstate = HasLong.Closed;
                            Close(TradeType.Buy);   //CLOSE

                            longstate = HasLong.Initial;
                        }
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (avr0 > top2)
                    {  // ロング：BandWalk
                        Print("{0}  トップ2σ再度上抜け      STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Top2_Above;
                        StateCount = 0;
                        if (longstate == HasLong.Open)
                            longstate = HasLong.ToClose;  // 利食いに向けたステータス。以降は１σタッチでもCLOSE
                    }
                    break;

                case States.Bottom1_Below:

                    if (avr0 >= main)
                    {  // ショート：CLOSE
                        Print("{0}  ボトム1σ→MAIN          STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        if (shortstate != HasShort.Initial)
                        {
                            shortstate = HasShort.Closed;
                            Close(TradeType.Sell); // CLOSE

                            shortstate = HasShort.Initial;
                        }
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (avr0 < bottom2)
                    {  // ショート：BandWalk
                        Print("{0}  ボトム2σ再度下抜け      STATE:{1}, StateCount: {2}, DI-: {3}, DI+: {4}, ADX: {5}", avr0, curstate, StateCount, diminus, diplus, diadx);
                        curstate = States.Bottom2_Below;
                        StateCount = 0;
                        if (shortstate == HasShort.Open)
                            shortstate = HasShort.ToClose;  // 利食いに向けたステータス。以降は１σタッチでもCLOSE
                    }
                    break;
            }
        }
    }
}

