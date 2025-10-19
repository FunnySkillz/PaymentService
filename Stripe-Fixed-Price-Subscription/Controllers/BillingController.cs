using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stripe;

namespace Stripe_Fixed_Price_Subscription.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BillingController : ControllerBase
    {
        private readonly IOptions<StripeOptions> options;

        public BillingController(IOptions<StripeOptions> options)
        {
            this.options = options;
            StripeConfiguration.ApiKey = options.Value.SecretKey;
        }

        [HttpGet("config")]
        public ActionResult<ConfigResponse> GetConfig()
        {

            var options = new PriceListOptions
            {
                LookupKeys = new List<string>
              {
                "sample_basic",
                "sample_premium"
              }
            };
            var service = new PriceService();
            var prices = service.List(options);

            return new ConfigResponse
            {
                PublishableKey = this.options.Value.PublishableKey,
                Prices = prices.Data
            };
        }

        [HttpPost("create-customer")]
        public ActionResult<CreateCustomerResponse> CreateCustomer([FromBody] CreateCustomerRequest req)
        {
            var options = new CustomerCreateOptions
            {
                Email = req.Email,
            };
            var service = new CustomerService();
            var customer = service.Create(options);

            // Set the cookie to simulate an authenticated user.
            // In practice, this customer.Id is stored along side your
            // user and retrieved along with the logged in user.
            HttpContext.Response.Cookies.Append("customer", customer.Id);

            return new CreateCustomerResponse
            {
                Customer = customer,
            };
        }

        [HttpPost("create-subscription")]
        public ActionResult<SubscriptionCreateResponse> CreateSubscription([FromBody] CreateSubscriptionRequest req)
        {
            var customerId = HttpContext.Request.Cookies["customer"];

            var options = new SubscriptionCreateOptions
            {
                Customer = customerId,
                Items = new List<SubscriptionItemOptions> { new() { Price = req.PriceId } },
                PaymentBehavior = "default_incomplete",
            };

            // You need the invoice expanded to access ConfirmationSecret
            options.AddExpand("latest_invoice");
            options.AddExpand("latest_invoice.confirmation_secret"); // harmless if already included

            var subscriptionService = new SubscriptionService();
            var invoiceService = new InvoiceService();

            try
            {
                var subscription = subscriptionService.Create(options);

                // Get the invoice object (expanded or fetch by id)
                var invoice = subscription.LatestInvoice as Invoice
                              ?? invoiceService.Get(subscription.LatestInvoiceId);

                var clientSecret = invoice.ConfirmationSecret?.ClientSecret;
                if (string.IsNullOrEmpty(clientSecret))
                {
                    // Rare edge case: not yet available
                    return BadRequest("Client secret not available yet on latest invoice.");
                }

                return new SubscriptionCreateResponse
                {
                    SubscriptionId = subscription.Id,
                    ClientSecret = clientSecret
                };
            }
            catch (StripeException e)
            {
                Console.WriteLine($"Failed to create subscription. {e}");
                return BadRequest(e.Message);
            }
        }


        [HttpGet("invoice-preview")]
        public ActionResult<InvoiceResponse> InvoicePreview(string subscriptionId, string newPriceId)
        {
            var customerId = HttpContext.Request.Cookies["customer"];
            if (string.IsNullOrWhiteSpace(customerId))
                return BadRequest("Missing customer cookie.");

            var subscriptionService = new SubscriptionService();
            var subscription = subscriptionService.Get(subscriptionId);
            if (subscription?.Items?.Data?.Count == 0)
                return BadRequest("Subscription has no items.");

            var invoiceService = new InvoiceService();

            // Create preview for swapping the existing item to a new Price
            var options = new InvoiceCreatePreviewOptions
            {
                Customer = customerId,
                Subscription = subscriptionId,
                SubscriptionDetails = new InvoiceSubscriptionDetailsOptions
                {
                    Items = new List<InvoiceSubscriptionDetailsItemOptions>
            {
                new()
                {
                    // keep the current item, but change its Price
                    Id = subscription.Items.Data[0].Id,
                    Price = newPriceId,
                    // optional:
                    // Quantity = 1,
                    // ProrationBehavior = "create_prorations"
                }
            }
                }
            };

            var preview = invoiceService.CreatePreview(options);

            return new InvoiceResponse { Invoice = preview };
        }

        [HttpPost("cancel-subscription")]
        public ActionResult<SubscriptionResponse> CancelSubscription([FromBody] CancelSubscriptionRequest req)
        {
            var service = new SubscriptionService();
            var subscription = service.Cancel(req.Subscription, null);
            return new SubscriptionResponse
            {
                Subscription = subscription,
            };
        }

        [HttpPost("update-subscription")]
        public ActionResult<SubscriptionResponse> UpdateSubscription([FromBody] UpdateSubscriptionRequest req)
        {
            var service = new SubscriptionService();
            var subscription = service.Get(req.Subscription);

            var options = new SubscriptionUpdateOptions
            {
                CancelAtPeriodEnd = false,
                Items = new List<SubscriptionItemOptions>
                {
                    new SubscriptionItemOptions
                    {
                        Id = subscription.Items.Data[0].Id,
                        Price = Environment.GetEnvironmentVariable(req.NewPrice.ToUpper()),
                    }
                }
            };
            var updatedSubscription = service.Update(req.Subscription, options);
            return new SubscriptionResponse
            {
                Subscription = updatedSubscription,
            };
        }

        [HttpGet("subscriptions")]
        public ActionResult<SubscriptionsResponse> ListSubscriptions()
        {
            var customerId = HttpContext.Request.Cookies["customer"];
            var options = new SubscriptionListOptions
            {
                Customer = customerId,
                Status = "all",
            };
            options.AddExpand("data.default_payment_method");
            var service = new SubscriptionService();
            var subscriptions = service.List(options);

            return new SubscriptionsResponse
            {
                Subscriptions = subscriptions,
            };
        }


        // helper: parse "pi_XXX" from "pi_XXX_secret_YYY"
        static string? ExtractPiId(string? clientSecret)
        {
            if (string.IsNullOrEmpty(clientSecret)) return null;
            var idx = clientSecret.IndexOf("_secret", System.StringComparison.Ordinal);
            if (idx <= 0) return null;
            var pi = clientSecret.Substring(0, idx);
            return pi.StartsWith("pi_") ? pi : null;
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    this.options.Value.WebhookSecret
                );
                Console.WriteLine($"Webhook: {stripeEvent.Type} ({stripeEvent.Id})");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Webhook signature verification failed: {e}");
                return BadRequest();
            }

            if (stripeEvent.Type == "invoice.payment_succeeded")
            {
                var evtInvoice = stripeEvent.Data.Object as Invoice;
                if (evtInvoice == null) return Ok();

                // Re-fetch with payments expanded (events can be slim)
                var invoiceService = new InvoiceService();
                var getOpts = new InvoiceGetOptions();
                getOpts.AddExpand("payments");
                Invoice? invoice = await invoiceService.GetAsync(evtInvoice.Id, getOpts);

                // Only for the first paid invoice on a new subscription
                if (invoice.BillingReason == "subscription_create")
                {
                    // 1) Prefer PaymentIntentId from the payments list
                    var paymentIntentId = invoice?.Payments?.Data?.FirstOrDefault()?.Payment.PaymentIntentId;

                    // 2) Fallback: parse from confirmation_secret (format "pi_XXX_secret_YYY")
                    if (string.IsNullOrEmpty(paymentIntentId))
                        paymentIntentId = ExtractPiId(invoice?.ConfirmationSecret?.ClientSecret);

                    if (!string.IsNullOrEmpty(paymentIntentId))
                    {
                        var pi = await new PaymentIntentService().GetAsync(paymentIntentId);
                        if (!string.IsNullOrEmpty(pi?.PaymentMethodId))
                        {
                            // In v49 LTS the subscription id is available as SubscriptionId
                            var subscriptionId = invoice?.Parent?.SubscriptionDetails.SubscriptionId;

                            if (!string.IsNullOrEmpty(subscriptionId))
                            {
                                var update = new SubscriptionUpdateOptions
                                {
                                    DefaultPaymentMethod = pi.PaymentMethodId
                                };
                                await new SubscriptionService().UpdateAsync(subscriptionId, update);
                                Console.WriteLine($"Set default PM for subscription {subscriptionId}: {pi.PaymentMethodId}");
                            }
                            else
                            {
                                Console.WriteLine("Invoice has no SubscriptionId; cannot set default PM.");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"PaymentIntent {paymentIntentId} has no PaymentMethodId.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Could not determine PaymentIntentId from invoice.");
                    }
                }

                Console.WriteLine($"Payment succeeded for invoice: {evtInvoice.Id}");
            }
            else if (stripeEvent.Type == "invoice.paid")
            {
                // post-trial provisioning, etc.
            }
            else if (stripeEvent.Type == "invoice.payment_failed")
            {
                // notify user / collect a new payment method
            }
            else if (stripeEvent.Type == "invoice.finalized")
            {
                // optional: store invoice locally
            }
            else if (stripeEvent.Type == "customer.subscription.deleted")
            {
                // handle cancellations
            }
            else if (stripeEvent.Type == "customer.subscription.trial_will_end")
            {
                // notify about trial end
            }

            return Ok();
        }

    }
}
