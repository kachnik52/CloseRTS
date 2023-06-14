using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockSharp.Algo;
using StockSharp.BusinessEntities;
using StockSharp.Logging;
using StockSharp.Messages;

namespace PrismaBoy
{
    sealed class CloseRts: MyBaseStrategy
    {
        /// <summary>
        /// Дневной проход РТС
        /// </summary>
        private readonly decimal _dayRate;                                              

        /// <summary>
        /// Вечерний проход РТС
        /// </summary>
        private readonly decimal _eveningRate;

        public CloseRts(List<Security> securityList, Dictionary<string, decimal> securityVolumeDictionary, TimeSpan timeFrame, decimal stopLossPercent, decimal takeProfitPercent, decimal dayRate, decimal eveningRate, bool loadActiveTrades)
            : base(securityList, securityVolumeDictionary, timeFrame, stopLossPercent, takeProfitPercent)
        {
            Name = "CloseRts";
            IsIntraDay = false;
            CloseAllPositionsOnStop = false;
            CancelOrdersWhenStopping = false;
            StopType = StopTypes.MarketLimitOfferForced;

            // В соответствии с параметрами конструктора
            _dayRate = dayRate;
            _eveningRate = eveningRate;

            if (loadActiveTrades)
            {
                LoadActiveTrades(Name);
            }
        }

        /// <summary>
        /// Событие старта стратегии
        /// </summary>
        protected override void OnStarted()
        {
            TimeToStopRobot = IsWorkContour
                                              ? new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 23,
                                                             50, 00)
                                              : new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 23,
                                                             49, 00);

            this.AddInfoLog("Стратегия запускается со следующими параметрами:" +
                            "\n\nСтоплосс, %: " + StopLossPercent +
                            "\nТейкпрофит, %: " + TakeProfitPercent +
                            "\nДневной проход, пт: " + _dayRate +
                            "\nВечерний проход, пт: " + _eveningRate);

            var prevClosingPrices = MainWindow.Instance.ReadClosingPrices(1);
            var prevClosingInfo = SecurityList.Aggregate("Данные на закрытие предыдущего дня:\n\n",
                                                         (current, security) =>
                                                         current +
                                                         (security.Code + " - " + prevClosingPrices[security.Code].Price +
                                                          " at " +
                                                          prevClosingPrices[
                                                              security.Code
                                                              ].Time.ToString(
                                                                  CultureInfo.
                                                                      CurrentCulture) +
                                                          "\n"));

            this.AddInfoLog(prevClosingInfo);

            base.OnStarted();
        }

        /// <summary>
        /// Метод-обработчик прихода новой свечки
        /// </summary>
        protected override void TimeFrameCome(object sender, MainWindow.TimeFrameEventArgs e)
        {
            base.TimeFrameCome(sender, e);

            // Если сейчас 10-05, то проверяем есть ли активные трейды
            if (e.MarketTime.AddSeconds(5).Hour == 10 && e.MarketTime.AddSeconds(5).Minute == 05)
            {
                foreach (var security in SecurityList.Where(security => ActiveTrades.Count(trade => trade.Security == security.Code) != 0))
                {
                    this.AddInfoLog("ВЫХОД ПО ВРЕМЕНИ. Московское время 10-05. Выходим 'по рынку'.");
                    ExitByTime(security);
                }

                return;
            }

            // Если сейчас не 23-45, то - ничего не делаем
            if (e.MarketTime.AddSeconds(5).Hour != 23 || e.MarketTime.AddSeconds(5).Minute != 45) return;

            // Если сейчас 23-45, то - проверяем есть ли активные сделки или уже выставленные ордера на вход и тогда проверяем условия
            foreach (var currentSecurity in SecurityList.Where(currentSecurity => ActiveTrades.Count(trade => trade.Security == currentSecurity.Code) == 0).Where(currentSecurity => !Orders.Any(
                order =>
                order.Security == currentSecurity && order.State == OrderStates.Active &&
                order.Comment.Contains("enter"))))
            {
                this.AddInfoLog("ВРЕМЯ. Московское время 23-45. Проверяем дневной и вечерний проход {0} на параметры для входа.", currentSecurity.Code);

                if (MainWindow.Instance.ReadClosingPrices(1) == null || MainWindow.Instance.ReadClosingPrices(1).Count == 0 || !MainWindow.Instance.ReadClosingPrices(1).ContainsKey(currentSecurity.Code))
                {
                    this.AddInfoLog("ОШИБКА. Не удалось прочитать цены закрытия предыдущего дня для {0}",
                                    currentSecurity.Code);
                    continue;
                }

                if (MainWindow.Instance.ReadEveningPrices(0) == null || MainWindow.Instance.ReadEveningPrices(0).Count == 0 || !MainWindow.Instance.ReadEveningPrices(0).ContainsKey(currentSecurity.Code))
                {
                    this.AddInfoLog("ОШИБКА. Не удалось прочитать цены закрытия дневной торговой сессии для {0}",
                                    currentSecurity.Code);
                    continue;
                }

                var closingPrice = MainWindow.Instance.ReadClosingPrices(1)[currentSecurity.Code].Price;
                var eveningPrice = MainWindow.Instance.ReadEveningPrices(0)[currentSecurity.Code].Price;


                this.AddInfoLog("ЦЕНЫ:\nЗакрытие предыдущего дня - {0}\nЗакрытие дневной торговой сессии - {1}",
                                closingPrice.ToString(CultureInfo.InvariantCulture),
                                eveningPrice.ToString(CultureInfo.InvariantCulture));

                if ((eveningPrice - closingPrice) > _dayRate)
                {
                    #region Выставляем заявку на ВХОД на продажу
                    var orderSell = new Order
                    {
                        Comment = Name + ", enter",
                        ExpiryDate = DateTime.Now.AddDays(1),
                        Portfolio = Portfolio,
                        Security = currentSecurity,
                        Type = OrderTypes.Limit,
                        Volume = SecurityVolumeDictionary[currentSecurity.Code],
                        Direction = Sides.Sell,
                        Price = currentSecurity.ShrinkPrice(e.LastBarsDictionary[currentSecurity.Code].Close)
                    };

                    this.AddInfoLog(
                        "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                        currentSecurity.Code, orderSell.Direction == Sides.Sell ? "продажу" : "покупку",
                        orderSell.Price.ToString(CultureInfo.InvariantCulture),
                        orderSell.Volume.ToString(CultureInfo.InvariantCulture),
                        currentSecurity.ShrinkPrice(orderSell.Price * (1 + StopLossPercent / 100)));

                    RegisterOrder(orderSell);

                    #endregion
                }
                else if ((closingPrice - eveningPrice) > _dayRate)
                {
                    if ((e.LastBarsDictionary[currentSecurity.Code].Close - eveningPrice) > _eveningRate)
                    {
                        #region Выставляем заявку на ВХОД на продажу
                        var orderSell = new Order
                        {
                            Comment = Name + ", enter",
                            ExpiryDate = DateTime.Now.AddDays(1),
                            Portfolio = Portfolio,
                            Security = currentSecurity,
                            Type = OrderTypes.Limit,
                            Volume = SecurityVolumeDictionary[currentSecurity.Code],
                            Direction = Sides.Sell,
                            Price = currentSecurity.ShrinkPrice(e.LastBarsDictionary[currentSecurity.Code].Close)
                        };

                        this.AddInfoLog(
                            "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                            currentSecurity.Code, orderSell.Direction == Sides.Sell ? "продажу" : "покупку",
                            orderSell.Price.ToString(CultureInfo.InvariantCulture),
                            orderSell.Volume.ToString(CultureInfo.InvariantCulture),
                            currentSecurity.ShrinkPrice(orderSell.Price * (1 + StopLossPercent / 100)));

                        RegisterOrder(orderSell);

                        #endregion
                    }
                    else if ((eveningPrice - e.LastBarsDictionary[currentSecurity.Code].Close) > _eveningRate)
                    {
                        #region Выставляем заявку на ВХОД на покупку
                        var orderBuy = new Order
                        {
                            Comment = Name + ", enter",
                            ExpiryDate = DateTime.Now.AddDays(1),
                            Portfolio = Portfolio,
                            Security = currentSecurity,
                            Type = OrderTypes.Limit,
                            Volume = SecurityVolumeDictionary[currentSecurity.Code],
                            Direction = Sides.Buy,
                            Price = currentSecurity.ShrinkPrice(e.LastBarsDictionary[currentSecurity.Code].Close)
                        };

                        this.AddInfoLog(
                            "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                            currentSecurity.Code, orderBuy.Direction == Sides.Sell ? "продажу" : "покупку",
                            orderBuy.Price.ToString(CultureInfo.InvariantCulture),
                            orderBuy.Volume.ToString(CultureInfo.InvariantCulture),
                            currentSecurity.ShrinkPrice(orderBuy.Price * (1 - StopLossPercent / 100)));

                        RegisterOrder(orderBuy);

                        #endregion
                    }
                }
            }
        }
        
        /// <summary>
        /// Метод "выхода по времени"
        /// </summary>
        private void ExitByTime(Security security)
        {
            var currentPosition = GetCurrentPosition(security);
            if (currentPosition == 0)
                return;

            var volume = Math.Abs(currentPosition);

            var exitByTimeOrder = new Order
            {
                Comment = Name + ",t",
                Type = OrderTypes.Limit,
                Portfolio = Portfolio,
                Security = security,
                Volume = volume,
                Direction = currentPosition > 0 ? Sides.Sell : Sides.Buy,
                Price = currentPosition < 0
                        ? security.ShrinkPrice(security.BestAsk.Price)
                        : security.ShrinkPrice(security.BestBid.Price),
            };

            // После срабатывания временного ордера, выводим сообщение в лог и останавливаем защитную стратегию

            exitByTimeOrder
                .WhenRegistered()
                .Once()
                .Do(() => this.AddInfoLog(
                        "ВЫХОД по ВРЕМЕНИ - {0}. Зарегистрирована заявка на выход из позиции по лучшей ценe {1} в стакане.",
                        security,
                        exitByTimeOrder.Direction == Sides.Buy ? "Ask" : "Bid"))
                .Apply(this);

            exitByTimeOrder
                .WhenMatched()
                .Do(() =>
                {
                    ActiveTrades = ActiveTrades.Where(activeTrade => activeTrade.Security != security.Code).ToList();

                    // Вызываем событие прихода изменения ActiveTrades
                    OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    this.AddInfoLog(
                        "ВЫХОД по ВРЕМЕНИ - {0}. Вышли из позиции по лучшей ценe {1} в стакане.",
                        security.Code,
                        exitByTimeOrder.Direction == Sides.Buy ? "Ask" : "Bid");
                })
                .Apply(this);

            // Регистрируем ордер
            this.AddInfoLog("Регистрируем ордер на выход по ВРЕМЕНИ");

            RegisterOrder(exitByTimeOrder);
        }
    }
}
