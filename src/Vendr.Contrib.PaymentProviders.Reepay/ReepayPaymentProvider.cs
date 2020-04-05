﻿using Flurl.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using Vendr.Contrib.PaymentProviders.Reepay;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

namespace Vendr.Contrib.PaymentProviders
{
    [PaymentProvider("reepay", "Reepay", "Reepay payment provider", Icon = "icon-invoice")]
    public class ReepayPaymentProvider : PaymentProviderBase<ReepaySettings>
    {
        public ReepayPaymentProvider(VendrContext vendr)
            : base(vendr)
        { }

        public override bool CanCancelPayments => true;
        public override bool CanCapturePayments => true;
        public override bool CanRefundPayments => true;
        public override bool CanFetchPaymentStatus => true;

        public override bool FinalizeAtContinueUrl => true;

        public override PaymentFormResult GenerateForm(OrderReadOnly order, string continueUrl, string cancelUrl, string callbackUrl, ReepaySettings settings)
        {
            var currency = Vendr.Services.CurrencyService.GetCurrency(order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new Exception("Currency must a valid ISO 4217 currency code: " + currency.Name);
            }

            var orderAmount = AmountToMinorUnits(order.TotalPrice.Value.WithTax).ToString("0", CultureInfo.InvariantCulture);

            var paymentMethods = settings.PaymentMethods?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(x => !string.IsNullOrWhiteSpace(x))
                   .Select(s => s.Trim())
                   .ToArray();

            string paymentFormLink = string.Empty;

            var chargeSessionId = order.Properties["reepayChargeSessionId"]?.Value;

            // https://docs.reepay.com/docs/new-web-shop

            try
            {
                var basicAuth = Base64Encode(settings.PrivateKey + ":");

                var payment = $"https://checkout-api.reepay.com/v1/session/charge"
                            .WithHeader("Authorization", "Basic " + basicAuth)
                            .WithHeader("Content-Type", "application/json")
                            .PostJsonAsync(new
                            {
                                order = new
                                {
                                    handle = order.OrderNumber,
                                    amount = orderAmount,
                                    currency = currencyCode,
                                    customer = new
                                    {
                                        email = order.CustomerInfo.Email,
                                        handle = order.CustomerInfo.CustomerReference,
                                        first_name = order.CustomerInfo.FirstName,
                                        last_name = order.CustomerInfo.LastName
                                    }
                                },
                                locale = settings.Lang,
                                settle = false,
                                payment_methods = paymentMethods != null && paymentMethods.Length > 0 ? "[" + string.Join(",", paymentMethods.Select(x => $"\"{x}\"")) + "]" : null,
                                accept_url = continueUrl,
                                cancel_url = cancelUrl
                            })
                            .ReceiveJson().Result;

                // Get charge session id
                chargeSessionId = payment.id;

                paymentFormLink = payment.url;
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<ReepayPaymentProvider>(ex, "Reepay - error creating payment.");
            }

            return new PaymentFormResult()
            {
                MetaData = new Dictionary<string, string>
                {
                    { "reepayChargeSessionId", chargeSessionId }
                },
                Form = new PaymentForm(paymentFormLink, FormMethod.Get)
                            .WithJsFile("https://checkout.reepay.com/checkout.js")
                            .WithJs(@"var rp = new Reepay.WindowCheckout('" + chargeSessionId + "');")
            };
        }

        public override string GetCancelUrl(OrderReadOnly order, ReepaySettings settings)
        {
            settings.MustNotBeNull("settings");
            settings.CancelUrl.MustNotBeNull("settings.CancelUrl");

            return settings.CancelUrl;
        }

        public override string GetErrorUrl(OrderReadOnly order, ReepaySettings settings)
        {
            settings.MustNotBeNull("settings");
            settings.ErrorUrl.MustNotBeNull("settings.ErrorUrl");

            return settings.ErrorUrl;
        }

        public override string GetContinueUrl(OrderReadOnly order, ReepaySettings settings)
        {
            settings.MustNotBeNull("settings");
            settings.ContinueUrl.MustNotBeNull("settings.ContinueUrl");

            return settings.ContinueUrl;
        }

        public override CallbackResult ProcessCallback(OrderReadOnly order, HttpRequestBase request, ReepaySettings settings)
        {
            try
            {
                // Process callback


                return new CallbackResult
                {
                    TransactionInfo = new TransactionInfo
                    {
                        AmountAuthorized = order.TotalPrice.Value.WithTax,
                        TransactionFee = 0m,
                        TransactionId = Guid.NewGuid().ToString("N"),
                        PaymentStatus = PaymentStatus.Authorized
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<ReepayPaymentProvider>(ex, "Reepay - ProcessCallback");
            }

            return CallbackResult.Empty;
        }

        public override ApiResult FetchPaymentStatus(OrderReadOnly order, ReepaySettings settings)
        {
            // Get charge: https://reference.reepay.com/api/#get-charge

            try
            {
                var basicAuth = Base64Encode(settings.PrivateKey + ":");
                var handle = order.OrderNumber;

                var payment = $"https://api.reepay.com/v1/charge/{handle}"
                            .WithHeader("Authorization", "Basic " + basicAuth)
                            .WithHeader("Content-Type", "application/json")
                            .GetJsonAsync<ReepayChargeDto>().Result;

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(payment),
                        PaymentStatus = GetPaymentStatus(payment)
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<ReepayPaymentProvider>(ex, "Reepay - FetchPaymentStatus");
            }

            return ApiResult.Empty;
        }

        public override ApiResult CancelPayment(OrderReadOnly order, ReepaySettings settings)
        {
            // Cancel charge: https://reference.reepay.com/api/#cancel-charge

            try
            {
                var basicAuth = Base64Encode(settings.PrivateKey + ":");
                var handle = order.OrderNumber;

                var payment = $"https://api.reepay.com/v1/charge/{handle}/cancel"
                            .WithHeader("Authorization", "Basic " + basicAuth)
                            .WithHeader("Content-Type", "application/json")
                            .PostAsync(null)
                            .ReceiveJson<ReepayChargeDto>().Result;

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = order.TransactionInfo.TransactionId,
                        PaymentStatus = GetPaymentStatus(payment)
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<ReepayPaymentProvider>(ex, "Reepay - CancelPayment");
            }

            return ApiResult.Empty;
        }

        public override ApiResult CapturePayment(OrderReadOnly order, ReepaySettings settings)
        {
            // Create charge: https://reference.reepay.com/api/#create-charge

            try
            {
                var basicAuth = Base64Encode(settings.PrivateKey + ":");
                var handle = order.OrderNumber;

                var payment = $"https://api.reepay.com/v1/charge"
                    .WithHeader("Authorization", "Basic " + basicAuth)
                    .WithHeader("Content-Type", "application/json")
                    .PostJsonAsync(new
                    {
                        handle = handle,
                        amount = AmountToMinorUnits(order.TransactionInfo.AmountAuthorized.Value),
                        settle = true
                    })
                    .ReceiveJson<ReepayChargeDto>().Result;
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<ReepayPaymentProvider>(ex, "Reepay - CapturePayment");
            }

            return ApiResult.Empty;
        }

        public override ApiResult RefundPayment(OrderReadOnly order, ReepaySettings settings)
        {
            // Create refund: https://reference.reepay.com/api/#create-refund

            try
            {
                var basicAuth = Base64Encode(settings.PrivateKey + ":");
                var handle = order.OrderNumber;

                var payment = $"https://api.reepay.com/v1/refund"
                    .WithHeader("Authorization", "Basic " + basicAuth)
                    .WithHeader("Content-Type", "application/json")
                    .PostJsonAsync(new
                    {
                        invoice = handle,
                        amount = AmountToMinorUnits(order.TransactionInfo.AmountAuthorized.Value)
                    })
                    .ReceiveJson<ReepayChargeDto>().Result;
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<ReepayPaymentProvider>(ex, "Reepay - RefundPayment");
            }

            return ApiResult.Empty;
        }

        protected PaymentStatus GetPaymentStatus(ReepayChargeDto payment)
        {
            // Possible Payment statuses:
            // - authorized
            // - settled
            // - failed
            // - cancelled
            // - pending

            if (payment.State == "authorized")
                return PaymentStatus.Authorized;

            if (payment.State == "settled")
                return PaymentStatus.Captured;

            if (payment.State == "failed")
                return PaymentStatus.Error;

            if (payment.State == "cancelled")
                return PaymentStatus.Cancelled;

            if (payment.State == "pending")
                return PaymentStatus.PendingExternalSystem;

            return PaymentStatus.Initialized;
        }

        protected string GetTransactionId(ReepayChargeDto payment)
        {
            return payment?.Transaction;
        }
    }
}
