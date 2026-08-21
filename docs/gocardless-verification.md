# GoCardless verification checklist

`GoCardlessService` was written **without access to the GoCardless sandbox or their
documentation** — both are unreachable from the environment it was built in. Its request
and response shapes come from documented knowledge, not from an observed exchange.

Everything around it is different: the order lifecycle, payment states, webhook
signature verification, idempotency and the publication rule are provider-independent
and covered by the test suite. Only the HTTP calls in this one class are unverified.

Until this checklist is complete, leave `Integrations:GoCardless:AccessToken` unset. The
stub is registered automatically when it is, and it refuses to authorise anything
outside Development.

## Before taking real payments

Run a full sandbox checkout and confirm each item.

### Creating the payment

- [ ] `POST /billing_requests` is the correct endpoint, and the envelope key is
      `billing_requests`.
- [ ] `payment_request.amount` is in the **smallest currency unit** (pence for GBP).
      The code multiplies by 100 — confirm £99.00 arrives as `9900`, not `99`.
- [ ] `payment_request.currency` accepts `"GBP"`.
- [ ] `metadata` accepts the `order_id` and `client_id` keys, and they come back on the
      webhook. If they do not, order matching relies solely on the payment id.
- [ ] The `GoCardless-Version` header value (`2015-07-06`) is still current.
- [ ] `Idempotency-Key` is the correct header name. **This is the one that matters most:**
      if it is wrong, a retried request could open a second billing request and charge
      the client twice.

### The hosted page

- [ ] `POST /billing_request_flows` is correct, and `links.billing_request` is where the
      billing request id belongs.
- [ ] The response field holding the hosted page URL is `authorisation_url`.
- [ ] `redirect_uri` and `exit_uri` behave as success and cancel destinations.

### Reading the result back

- [ ] `GET /billing_requests/{id}` returns a `status` field.
- [ ] `"fulfilled"` is the status meaning the client completed the journey. If a
      different value indicates success, `CompleteCheckoutAsync` will report a genuine
      payment as incomplete.
- [ ] `links.payment` and `links.mandate` are present once fulfilled.

### Webhooks

- [ ] The signature header is named `Webhook-Signature`.
- [ ] The signature is HMAC-SHA256 of the raw body, hex encoded, using the webhook
      secret. `WebhookVerifierTests` proves the computation; this confirms the provider
      computes it the same way.
- [ ] The event actions in `PaymentWebhookProcessor.MapPaymentStatus` match what the
      sandbox actually sends. Unknown actions are recorded and ignored, so a missing one
      is a silent no-op rather than an error — check the recorded events for actions
      that mapped to nothing.
- [ ] A non-200 response causes a retry, and a repeat delivery of the same event id is
      skipped rather than reapplied.

### End to end

- [ ] A successful sandbox payment confirms the order and publishes the portfolio.
- [ ] A cancelled payment leaves the portfolio unpublished and takes no money.
- [ ] Closing the browser mid-payment still results in publication once the webhook
      arrives, with no visit to the success page.
- [ ] The amount recorded on the order is exactly £3,499.00.
