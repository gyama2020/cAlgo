using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class gyBolliBanBreakcBot : Robot
    {
        // cBotタイマー
        [Parameter("cBot AUTO STOP", Group = "cBot AUTO STOP", DefaultValue = false)]
        public Boolean cbotautostop { get; set; }
        // デフォルト 1440 は5m で月曜から金曜まで5日分
        [Parameter("cBot OFF Timer (candles)", Group = "cBot AUTO STOP", DefaultValue = 1440, MinValue = 5)]
        public int cbottimeout { get; set; }
        public int cbottimer = 0;

        // Trade
        [Parameter("Buy", Group = "Trade", DefaultValue = true)]
        public Boolean actbuy { get; set; }
        [Parameter("Sell", Group = "Trade", DefaultValue = true)]
        public Boolean actsell { get; set; }
        // 金曜日は取引しない
        [Parameter("no Trade on Friday", Group = "Trade", DefaultValue = true)]
        public Boolean fridayoff { get; set; }
        // 月曜朝のギャップ避け Sunday UTC 22:00
        [Parameter("Monday start at AM7:00(UTC+9)", Group = "Trade", DefaultValue = true)]
        public Boolean mondaymorning { get; set; }

        // Order
        [Parameter("Quantity (Lots)", Group = "Volume", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double Quantity { get; set; }
        [Parameter("Stop Loss (pips)", Group = "Protection", DefaultValue = 70, MinValue = 1)]
        public int StopLossInPips { get; set; }
        [Parameter("Take Profit (pips)", Group = "Protection", DefaultValue = 500, MinValue = 1)]
        public int TakeProfitInPips { get; set; }

        // TrailingStopLoss
        [Parameter("CLOSE処理にTSLを使用", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = true)]
        public Boolean bCloseTSL { get; set; }
        [Parameter("Trailing Stop Loss (pips)", Group = "CLOSE処理にトレイリングストップを実行", DefaultValue = 30, MinValue = 1)]
        public int TrailStopLossInPips { get; set; }

        // BollingerBands
        [Parameter("Source", Group = "BollingerBands")]
        public DataSeries srcBB { get; set; }
        // 1h -> 5m 調整 　参考）14*12=168、 25*12=300
        // 分析対象として1hを使用するがエントリーの精度を上げるため5mで使用する
        [Parameter("Periods", Group = "BollingerBands", DefaultValue = 300)]
        public int prdBB { get; set; }
        [Parameter("Deviations", Group = "BollingerBands", DefaultValue = 1)]
        public double devBB1 { get; set; }
        [Parameter("撤退基準に使用", Group = "BollingerBands", DefaultValue = false)]
        public Boolean bCloseBB { get; set; }
        [Parameter("Deviations", Group = "BollingerBands", DefaultValue = 2)]
        public double devBB2 { get; set; }
        [Parameter("MAType", Group = "BollingerBands")]
        public MovingAverageType matBB { get; set; }
        private BollingerBands bbnd1;
        private BollingerBands bbnd2;

        // Trigger MovingAverage
        // トリガーに使用。現在値でなくMAにして調整の幅を持たせる
        [Parameter("Source", Group = "Trigger MovingAverage")]
        public DataSeries srcSMA { get; set; }
        [Parameter("Periods", Group = "Trigger MovingAverage", DefaultValue = 3)]
        public int prdSMA { get; set; }
        [Parameter("MAType", Group = "Trigger MovingAverage")]
        private SimpleMovingAverage sma1;

        // 直近のMAINから2σまでのスピードで勢いを計測（トレンド発生の可能性）
        [Parameter("STATE 0 : MAIN to 2sigma", Group = "Momentum (Candles)", DefaultValue = 24)]
        public int lastmain { get; set; }
        // モメンタム計測用のカウンタ
        // ステータス変化 or MAINタッチでリセット
        public int cnt5 = 0;

        // 値動きを捕捉し状態をステータス管理
        // 0：初期値
        // 1：STATE=0 で2σトップへ到達、3：STATE=1 で2σトップを下抜け、5：STATE=3 で1σトップまで到達、7：STATE=5 でMAINへ到達
        // 2：STATE=0 で2σボトムへ到達、4：STATE=2 で2σボトムを上抜け、6：STATE=4 で1σボトムまで到達、8：STATE=6 でMAINへ到達
        public int status = 0;

        // 各ステータス間のタイムアウト（足数）
        // 要チューニング
        [Parameter("STATE: 1 or 2", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int timeout1 { get; set; }
        [Parameter("STATE: 3 or 4", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int timeout2 { get; set; }
        [Parameter("STATE: 5 or 6", Group = "TimeOut (Candles)", DefaultValue = 72)]
        public int timeout3 { get; set; }

        // タイムアウト制御用のカウンタ
        // ステータス変化でリセット
        public int cnt = 0;
        
        protected override void OnStart()
        {
            Print("START gyBolliBanBreak: {0}", Server.Time.ToLocalTime());
            bbnd1 = Indicators.BollingerBands(srcBB, prdBB, devBB1, matBB);
            bbnd2 = Indicators.BollingerBands(srcBB, prdBB, devBB2, matBB);
            sma1 = Indicators.SimpleMovingAverage(srcSMA, prdSMA);
        }

        protected override void OnStop()
        {
            Print("STOP gyBolliBanBreak: {0}", Server.Time.ToLocalTime());
        }

        protected override void OnError(Error error)
        {
            Print(error.Code);
            if (error.Code == ErrorCode.NoMoney)
                Stop();
        }

        private void Close(TradeType tradeType)
        {
            foreach (var position in Positions.FindAll("gyBolliBanBreak", SymbolName, tradeType))
            {
                ClosePosition(position);
                Print("[CLOSE ] [ {0} ] STATUS : {1}, Count : {2}", tradeType, status, cnt);
            }
        }

        private void Open(TradeType tradeType)
        {
            var position = Positions.Find("gyBolliBanBreak", SymbolName, tradeType);
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(Quantity);
            if (position == null)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "gyBolliBanBreak", StopLossInPips, TakeProfitInPips);
                Print("[ OPEN ] [ {0} ] STATUS : {1}, Count : {2}, LastMAIN : {3}", tradeType, status, cnt, lastmain);
            }
        }

        private void SetTSL(TradeType tradeType)
        {
            double? newstop;
            foreach (var position in Positions.FindAll("gyBolliBanBreak", SymbolName, tradeType))
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
            if ((!(fridayoff == true && Server.Time.DayOfWeek == DayOfWeek.Friday)) && !(mondaymorning == true && (Server.Time.DayOfWeek == DayOfWeek.Sunday && Server.Time.Hour < 22)))
               { 
                var top1 = bbnd1.Top.Last(0);
                var bottom1 = bbnd1.Bottom.Last(0);
                var main = bbnd1.Main.LastValue;
                var top2 = bbnd2.Top.Last(0);
                var bottom2 = bbnd2.Bottom.Last(0);
                var avr1 = sma1.Result.LastValue;

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
                if ((sma1.Result.Last(1) < bbnd1.Main.Last(1)) && (avr1 >= bbnd1.Main.Last(1)))
                    cnt5 = 0;  // MAINと交差したらリセット
                else if ((sma1.Result.Last(1) > bbnd1.Main.Last(1)) && (avr1 <= bbnd1.Main.Last(1)))
                    cnt5 = 0;  // MAINと交差したらリセット
                else
                    cnt5 = cnt5 + 1;

                    // ステータス変化とトレード
                    switch (status)
                    {
                        case 0:
                            if (cnt5 < lastmain)
                            {  // エントリーの条件：直近のMAIN交差から2σまでのスピードが一定以上
                                if (actsell == true && avr1 < bottom2)
                                {
                                    Print("{0}  2σボトム下抜け         STATE:{1}, cnt:{2}, cnt5:{3}", avr1, status, cnt, cnt5);
                                    Open(TradeType.Sell);  //ショート
                                    status = 1;
                                    cnt = 0;
                                }
                                else if (actbuy == true && avr1 > top2)
                                {
                                    Print("{0}  2σトップ上抜け         STATE:{1}, cnt:{2}, cnt5:{3}", avr1, status, cnt, cnt5);
                                    Open(TradeType.Buy);  //ロング
                                    status = 2;
                                    cnt = 0;
                                }
                            }
                            break;

                        case 1:
                            if (timeout1 < cnt)
                            { // ショート：BandWalk
                                Print("{0}  2σ滞在時間 閾値超過     STATE:{1},cnt:{2}", avr1, status, cnt);
                                if (bCloseTSL == true)
                                    SetTSL(TradeType.Sell);  //TrailingSLで利を伸ばす
                                else
                                    Close(TradeType.Sell);   //CLOSE
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 > bottom2)
                            {
                                Print("{0}  2σボトム上抜け          STATE:{1}, cnt:{2}, cnt5:{3}", avr1, status, cnt, cnt5);
                                status = 3;
                                cnt = 0;
                            }
                            break;

                        case 2:
                            if (timeout1 < cnt)
                            { // ロング：BandWalk
                                Print("{0}  2σOVER タイムアウト     STATE:{1},cnt:{2}", avr1, status, cnt);
                                if (bCloseTSL == true)
                                    SetTSL(TradeType.Buy);  //TrailingSLで利を伸ばす
                                else
                                    Close(TradeType.Buy);   //CLOSE
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 < top2)
                            {
                                Print("{0}  2σトップ下抜け          STATE:{1}, cnt:{2}, cnt5:{3}", avr1, status, cnt, cnt5);
                                status = 4;
                                cnt = 0;
                            }
                            break;

                        case 3:
                            if (timeout2 < cnt)
                            {  // ショート：微妙ライン
                                Print("{0}  2σ～1σ タイムアウト    STATE:{1},cnt:{2}", avr1, status, cnt);
                                if (bCloseTSL == true)
                                    SetTSL(TradeType.Sell);  //TrailingSLで利を伸ばす
                                else
                                    Close(TradeType.Sell);   //CLOSE
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 > bottom1)
                            {
                                if (bCloseBB == true)
                                { // ショート：撤退CLOSE
                                    Print("{0}  ボトム2σ→1σ CLOSE     STATE:{1},cnt:{2}", avr1, status, cnt);
                                    Close(TradeType.Sell);
                                    status = 0;
                                    cnt = 0;
                                }
                                else
                                {
                                    Print("{0}  ボトム2σ→1σ           STATE:{1},cnt:{2}", avr1, status, cnt);
                                    status = 5;
                                    cnt = 0;
                                }
                            }
                            else if (avr1 < bottom2)
                            {  // ショート：BandWalk
                                Print("{0}  ボトム2σ再度下抜け      STATE:{1},cnt:{2}", avr1, status, cnt);
                                status = 1;  //ステータス復帰
                                cnt = 0;
                            }
                            break;

                        case 4:
                            if (timeout2 < cnt)
                            {  // ロング：微妙ライン
                                Print("{0}  2σ～1σ タイムアウト    STATE:{1},cnt:{2}", avr1, status, cnt);
                                if (bCloseTSL == true)
                                    SetTSL(TradeType.Buy);  //TrailingSLで利を伸ばす
                                else
                                    Close(TradeType.Buy);   //CLOSE
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 < top1)
                            {
                                if (bCloseBB == true)
                                { // ロング：撤退CLOSE
                                    Print("{0}  トップ2σ→1σ CLOSE     STATE:{1},cnt:{2}", avr1, status, cnt);
                                    Close(TradeType.Buy);
                                    status = 0;
                                    cnt = 0;
                                }
                                else
                                {
                                    Print("{0}  トップ2σ→1σ           STATE:{1},cnt:{2}", avr1, status, cnt);
                                    status = 6;
                                    cnt = 0;
                                }
                            }
                            else if (avr1 > top2)
                            {  // ロング：BandWalk
                                Print("{0}  トップ2σ再度上抜け      STATE:{1},cnt:{2}", avr1, status, cnt);
                                status = 2;  //ステータス復帰
                                cnt = 0;
                            }
                            break;

                        case 5:
                            if (timeout3 < cnt)
                            {  // ショート：微妙ライン(バンド拡大期なら伸ばせる水準)
                                Print("{0}  ボトム1σ～MAIN タイムアウト   STATE:{1},cnt:{2}", avr1, status, cnt);
                                if (bCloseTSL == true)
                                    SetTSL(TradeType.Sell);  //TrailingSLで利を伸ばす
                                else
                                    Close(TradeType.Sell);   //CLOSE
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 >= main)
                            {  // ショート：撤退CLOSE
                                    Print("{0}  ボトム1σ→MAIN           STATE:{1},cnt:{2}", avr1, status, cnt);
                                    Close(TradeType.Sell);
                                    status = 0;
                                    cnt = 0;
                            }
                            else if (avr1 < bottom1)
                            {  // ショート：BandWalk
                                Print("{0}  ボトム1σ再度下抜け      STATE:{1},cnt:{2}", avr1, status, cnt);
                                status = 3;  //ステータス復帰
                                cnt = 0;
                            }
                            break;

                        case 6:
                            if (timeout3 < cnt)
                            {  // ロング：微妙ライン(バンド拡大期なら伸ばせる水準)
                                Print("{0}  トップ1σ～MAIN タイムアウト STATE:{1},cnt:{2}", avr1, status, cnt);
                                if (bCloseTSL == true)
                                    SetTSL(TradeType.Buy);  //TrailingSLで利を伸ばす
                                else
                                    Close(TradeType.Buy);   //CLOSE
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 <= main)
                            { // ロング：撤退CLOSE
                                Print("{0}  トップ1σ→MAIN         STATE:{1},cnt:{2}", avr1, status, cnt);
                                Close(TradeType.Buy);
                                status = 0;
                                cnt = 0;
                            }
                            else if (avr1 > top1)
                            {  // ロング：BandWalk
                                Print("{0}  トップ1σ再度上抜け     STATE:{1},cnt:{2}", avr1, status, cnt);
                                status = 4;  //ステータス復帰
                                cnt = 0;
                            }
                            break;
                    }
            }
        }
    }
}

