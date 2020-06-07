using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GyBolliBanRangecBot : Robot
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
        // 月曜朝のギャップ避け Sunday UTC 22:00
        [Parameter("週初はAM7:00開始", Group = "Trade", DefaultValue = true)]
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
        [Parameter("Trailing Stop Loss (pips)", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = 15, MinValue = 1)]
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
        // フィルターに使用。発注時ではなくCLOSEやタイムアウト時の継続判断に使用
        private DirectionalMovementSystem FilterADX;
        private double diplus;
        private double diminus;
        private double diadx;
        [Parameter("ADX Period", DefaultValue = 14)]
        public int PeriodADX { get; set; }

        // 値動きを捕捉し状態管理
        public enum States
        {
            Initial = 0,                // 0：初期値
            Top2_Above = 1,             // 1：STATE=0 で2σトップ上抜け
            Bottom2_Below = 2,          // 2：STATE=0 で2σボトム下抜け
            Top2_Below = 3,             // 3：STATE=1 で2σトップを下抜け
            Bottom2_Above = 4,          // 4：STATE=2 で2σボトムを上抜け
            Top2_to_Top1 = 5,           // 5：STATE=3 で1σトップまで到達
            Bottom2_to_Bottom1 = 6,     // 6：STATE=4 で1σボトムまで到達
            Top1_to_Main = 7,           // 7：STATE=5 でMAINへ到達
            Bottom1_to_Main = 8         // 8：STATE=6 でMAINへ到達
        }
        public States curstate = 0;

        // 各ステータス間のタイムアウト（足数）
        // 要チューニング
        [Parameter("STATE: 1 to 3 ( 2 to 4 )", Group = "TimeOut (Candles)", DefaultValue = 40)]
        public int TimeOut1 { get; set; }
        [Parameter("STATE: 3 to 5 ( 4 to 6 )", Group = "TimeOut (Candles)", DefaultValue = 60)]
        public int TimeOut2 { get; set; }
        [Parameter("STATE: 5 to 7 ( 6 to 8 )", Group = "TimeOut (Candles)", DefaultValue = 60)]
        public int TimeOut3 { get; set; }
        [Parameter("STATE: 7 ( 8 )", Group = "TimeOut (Candles)", DefaultValue = 60)]
        public int TimeOut4 { get; set; }

        // タイムアウト制御用のカウンタ
        // ステータス変化でリセット
        public int StateCount = 0;

        // BandWalk真っ最中に何度もINしないように直近のMAINから2σまでのスピードでフィルタ
        [Parameter("BandWalk対策：MAIN～2σ", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int TimeOut5 { get; set; }
        // タイムアウト制御用のカウンタ
        // ステータス変化 or タイムアウトでリセット
        public int BwCount = 0;

        protected override void OnStart()
        {
            Positions.Closed += PositionsOnClosed;
            Print("START GyBolliBanRange: {0}", Server.Time.ToLocalTime());
            BolliBan1 = Indicators.BollingerBands(SourceBB, PeriodBB, DeviateBB1, MaTypeBB);
            BolliBan2 = Indicators.BollingerBands(SourceBB, PeriodBB, DeviateBB2, MaTypeBB);
            SignalMA = Indicators.MovingAverage(SourceSignalMA, PeriodSignalMA, MaTypeSignal);
            FilterADX = Indicators.DirectionalMovementSystem(PeriodADX);
        }

        protected override void OnStop()
        {
            Print("STOP GyBolliBanRange: {0}", Server.Time.ToLocalTime());
        }

        protected override void OnError(Error error)
        {
            Print(error.Code);
            if (error.Code == ErrorCode.NoMoney)
                Stop();
        }

        private void Close(TradeType tradeType)
        {
            foreach (var position in Positions.FindAll("GyBolliBanRange", SymbolName, tradeType))
            {
                ClosePosition(position);
                Print("[CLOSE ] [ {0} ] States : {1}, Count : {2}", tradeType, curstate, StateCount);
            }
        }

        private void PositionsOnClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            var reas = args.Reason;
            Print("Position closed with {0} profit.    Reason : {1}", pos.GrossProfit,reas);
        }
        private void Open(TradeType tradeType)
        {
            var position = Positions.Find("GyBolliBanRange", SymbolName, tradeType);
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(Quantity);
            if (position == null)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "GyBolliBanRange", StopLossInPips, TakeProfitInPips);
                Print("[ OPEN ] [ {0} ] States : {1}, Count : {2}", tradeType, curstate, StateCount);
            }
        }

        private void SetTSL(TradeType tradeType)
        {
            double? newstop;
            foreach (var position in Positions.FindAll("GyBolliBanRange", SymbolName, tradeType))
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
          //  および　週初開始時刻設定（月曜日は朝一ギャップを除外したいのでAM7:00から(UTC+9)
            if (!(FirstFridayOff == true && Server.Time.Day < 9 && Server.Time.DayOfWeek == DayOfWeek.Friday)
                && !(MondayMorning == true && Server.Time.DayOfWeek == DayOfWeek.Sunday && Server.Time.Hour < 22))
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
            var signal = SignalMA.Result.LastValue;
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

            // 基準線の鮮度
            if ((SignalMA.Result.Last(1) < BolliBan1.Main.Last(1)) && (signal >= BolliBan1.Main.Last(1)))
                BwCount = 0;
            // MAINと交差したらリセット
            else if ((SignalMA.Result.Last(1) > BolliBan1.Main.Last(1)) && (signal <= BolliBan1.Main.Last(1)))
                BwCount = 0;
            else
                // MAINと交差したらリセット
                BwCount = BwCount + 1;

            // ステータス変化とトレード
            switch (curstate)
            {
                case States.Initial:
                    if (BwCount < TimeOut5)
                    { // BandWalk中のエントリーを排除するためMAIN交差からのタイムアウトを設定
                        if (ActSell == true && signal >= top2)
                        {
                            Print("{0}  2σトップ到達          STATE: {1}, StateCount: {2}", signal, curstate, StateCount);
                            curstate = States.Top2_Above;
                            StateCount = 0;
                        }
                        else if (ActBuy == true && signal <= bottom2)
                        {
                            Print("{0}  2σボトム到達          STATE: {1}, StateCount: {2}", signal, curstate, StateCount);
                            curstate = States.Bottom2_Below;
                            StateCount = 0;
                        }
                    }
                    break;

                case States.Top2_Above:
                    if (TimeOut1 < StateCount && diminus < diplus) // DI-ならタイムアウトしない
                    {
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal < top2)
                    {
                        Print("{0}  2σトップ下抜け          STATE: {1}, StateCount: {2}, ADX: [{3}]", signal, curstate, StateCount, diadx);
                        if (diadx > 40 && diplus > diminus)  // ADX 40以上かつ DI+ 強い場合はOpen見送り
                            curstate = States.Initial;
                        else
                        { 
                            if (CheckOpen())
                            {
                                Open(TradeType.Sell);  //ショート
                                curstate = States.Top2_Below;
                            } else
                                curstate = States.Initial;
                        }
                        StateCount = 0;
                    }
                    break;

                case States.Bottom2_Below:
                    if (TimeOut1 < StateCount && diplus < diminus)
                    {
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal > bottom2)
                    {
                        Print("{0}  2σボトム上抜け          STATE: {1}, StateCount: {2}, ADX: [{3}]", signal, curstate, StateCount, diadx);
                        if (diadx > 40 && diminus > diplus)  // ADX 40以上かつ DI- 強い場合はOpen見送り
                            curstate = States.Initial;
                        else
                        {
                            if (CheckOpen())
                            {
                                Open(TradeType.Buy);  //ロング
                                curstate = States.Bottom2_Above;
                            }
                            else
                                curstate = States.Initial;
                        }
                        StateCount = 0;
                    }
                    break;

                case States.Top2_Below:
                    if (TimeOut2 < StateCount && diminus < diplus)
                    { // ショート：タイムアウト & DI+ -> CLOSE
                        Print("{0}  タイムアウト             STATE: {1},StateCount: {2}, DI+: [{3}]", signal, curstate, StateCount, diplus);
                        Close(TradeType.Sell);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal <= top1)
                    {
                        Print("{0}  トップ2σ→1σ到達       STATE: {1}, StateCount: {2}", signal, curstate, StateCount);
                        curstate = States.Top2_to_Top1;
                        StateCount = 0;
                    }
                    break;

                case States.Bottom2_Above:
                    if (TimeOut2 < StateCount && diplus < diminus)
                    { // ロング：タイムアウト & DI- -> CLOSE
                        Print("{0}  タイムアウト             STATE: {1}, StateCount: {2}, DI-: [{3}]", signal, curstate, StateCount, diminus);
                        Close(TradeType.Buy);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal >= bottom1)
                    {
                        Print("{0}  ボトム2σ→1σ到達       STATE:{1},StateCount:{2}", signal, curstate, StateCount);
                        curstate = States.Bottom2_to_Bottom1;
                        StateCount = 0;
                    }
                    break;

                case States.Top2_to_Top1:
                    if (TimeOut3 < StateCount && diminus < diplus && diadx > 30)
                    { // ショート：タイムアウト & DI+ & ADX>30 -> CLOSE
                        Print("{0}  タイムアウト             STATE: {1}, StateCount: {2}, DI+: [{3}], ADX: [{4}]", signal, curstate, StateCount, diplus, diadx);
                        Close(TradeType.Sell);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal <= main)
                    {
                        Print("{0}  MAIN到達                 STATE: {1}, StateCount: {2}", signal, curstate, StateCount);
                        curstate = States.Top1_to_Main;
                        StateCount = 0;
                    }
                    else if (signal >= top2)
                    { // ショート：1σ到達後の2σ戻し、撤退CLOSE
                        Print("{0}  1σ到達後の2σ戻し       STATE: {1}, StateCount: {2}, DI+: [{3}]", signal, curstate, StateCount, diplus);
                        Close(TradeType.Sell);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal <= bottom1)
                    { // ショート：ボトム1σ到達、CLOSE
                        // 本来はMAIN到達を経由するはずだが一応判定、1σとMAINが期間内で同時到達はあり得る
                        Close(TradeType.Sell);
                        Print("<< 要デバッグ >>:{0} [Short] MAIN経由なしでCLOSE STATE:{1},StateCount:{2}", signal, curstate, StateCount);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    break;

                case States.Bottom2_to_Bottom1:
                    if (TimeOut3 < StateCount && diplus < diminus && diadx > 30)
                    { // ロング：タイムアウト & DI- & ADX>30 -> CLOSE
                        Print("{0}  タイムアウト             STATE: {1}, StateCount: {2}, DI-: [{3}], ADX: [{4}]", signal, curstate, StateCount, diminus, diadx);
                        Close(TradeType.Buy);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal >= main)
                    {
                        Print("{0}  MAIN到達                 STATE:{1},StateCount:{2}", signal, curstate, StateCount);
                        curstate = States.Bottom1_to_Main;
                        StateCount = 0;
                    }
                    else if (signal <= bottom2)
                    { // ロング：1σ到達後の2σ戻し、撤退CLOSE
                        Print("{0}  1σ到達後の2σ戻し       STATE:{1},StateCount:{2}, DI-: [{3}]", signal, curstate, StateCount, diminus);
                        Close(TradeType.Buy);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal >= top1)
                    { // ロング：トップ1σ到達、CLOSE
                        // 本来はMAIN到達を経由するはずだが一応判定、1σとMAINが期間内で同時到達はあり得る
                        Close(TradeType.Buy);
                        Print("<< 要デバッグ >>:{0} [Long] MAIN経由なしでCLOSE STATE:{1},StateCount:{2}", signal, curstate, StateCount);
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    break;

                case States.Top1_to_Main:
                    if (TimeOut4 < StateCount && diminus < diplus)
                    { // ショート：タイムアウト & DI+ -> CLOSE
                        Print("{0}  タイムアウト             STATE: {1}, StateCount: {2}, DI+: [{3}]", signal, curstate, StateCount, diplus);
                        if (CloseTSL == true)
                            SetTSL(TradeType.Sell);  //TrailingSLで利を伸ばす
                        else
                            Close(TradeType.Sell);   //CLOSE
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal <= bottom1)
                    {
                        Print("{0}  ボトム1σ到達            STATE: {1}, StateCount: {2}, DI-: [{3}]", signal, curstate, StateCount, diminus);
                        if (CloseTSL == true)
                            SetTSL(TradeType.Sell);  //TrailingSLで利を伸ばす
                        else
                            Close(TradeType.Sell);   //CLOSE
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal > top1)
                    { // ショート：MAIN到達後の戻し & DI+ -> 撤退CLOSE
                        Print("{0}  MAIN到達後の戻し         STATE: {1}, StateCount: {2}, DI+: [{3}]", signal, curstate, StateCount, diplus);
                        if (diminus < diplus)
                        {
                            if (CloseTSL == true)
                            {
                                Close(TradeType.Sell);
                                curstate = States.Initial;
                            }
                        }
                        else
                            curstate = States.Top2_to_Top1;
                        StateCount = 0;
                    }
                    break;

                case States.Bottom1_to_Main:
                    if (TimeOut4 < StateCount && diplus < diminus)
                    { // ロング：タイムアウト & DI- -> CLOSE
                        Print("{0}  タイムアウト             STATE: {1}, StateCount: {2}, DI-: [{3}]", signal, curstate, StateCount, diminus);
                        if (CloseTSL == true)
                            SetTSL(TradeType.Buy);  //TrailingSLで利を伸ばす
                        else
                            Close(TradeType.Buy);   //CLOSE
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal >= top1)
                    {
                        Print("{0}  トップ1σ到達            STATE: {1}, StateCount: {2}, DI+: [{3}]", signal, curstate, StateCount, diplus);
                        if (CloseTSL == true)
                            SetTSL(TradeType.Buy);  //TrailingSLで利を伸ばす
                        else
                            Close(TradeType.Buy);   //CLOSE
                        curstate = States.Initial;
                        StateCount = 0;
                    }
                    else if (signal <= bottom1)
                    { // ロング：MAIN到達後の戻し & DI- -> CLOSE撤退
                        Print("{0}  MAIN到達後の戻し         STATE: {1}, StateCount: {2}, DI-: [{3}]", signal, curstate, StateCount, diminus);
                        if (diplus < diminus)
                        {
                            Close(TradeType.Buy);
                            curstate = States.Initial;
                        }
                        else
                            curstate = States.Bottom2_to_Bottom1;
                        StateCount = 0;
                    }
                    break;
            }
        }
    }
}
