# GoHighLevel verification checklist

`HighLevelService` was written **without access to GoHighLevel's API or documentation** —
both are unreachable from the environment it was built in. The endpoint, the
custom-field payload shape and the field keys come from documented knowledge, not from
an observed exchange.

Everything around it is different: building the field set, the retry and backoff, the
staff alerting, and the rule that a CRM problem never disturbs a portfolio are all
provider-independent and covered by tests.

Until this checklist is complete, leave `Integrations:HighLevel:ApiKey` unset. The stub
is registered automatically when it is, and it logs rather than sends.

## Before relying on the CRM sync

- [ ] `PUT /contacts/{contactId}` is the correct endpoint for updating a contact, and
      `Integrations:HighLevel:BaseUrl` points at the right host.
- [ ] The `Version` header value (`2021-07-28`) is still accepted.
- [ ] Authentication is a Bearer token in the `Authorization` header. If the account
      uses OAuth rather than a private integration key, this needs a token exchange
      adding.
- [ ] The custom-field payload shape is right. The code sends
      `{"customFields":[{"key":"...","field_value":"..."}]}` — confirm whether the API
      expects `key`, `id`, or both, and whether the value property is `field_value` or
      `value`.
- [ ] **The six custom fields exist in MSM's GoHighLevel account** with keys matching
      `CrmFieldKeys`: `portfolio_url`, `portfolio_status`, `purchase_status`,
      `purchase_date`, `maintenance_status`, `portfolio_published_date`. A key that does
      not exist is the most likely silent failure: the call may succeed while writing
      nothing.
- [ ] Dates are accepted as `yyyy-MM-dd`.
- [ ] A contact id that does not exist returns 404, so it is treated as permanent rather
      than retried forever.

## End to end

- [ ] Publishing a portfolio updates the contact with the live URL and a status of
      "Live" within a couple of minutes.
- [ ] Unpublishing updates the status and clears the URL.
- [ ] A confirmed purchase sets the purchase status and date.
- [ ] A maintenance failure and its resolution both reach the contact.
- [ ] Turning the CRM off mid-run leaves portfolios completely unaffected, and the
      pending updates arrive once it returns.
- [ ] An existing GoHighLevel automation triggered by `portfolio_status` fires as MSM
      expects.
