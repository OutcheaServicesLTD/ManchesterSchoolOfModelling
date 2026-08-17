# Putting a preview online

A demonstration site MSM can click through on a real address. **Not the live system** —
see [What this preview is not](#what-this-preview-is-not) before showing it to anyone.

Written to be followed without using a terminal.

## Why Render

Recommended for this stage because the whole setup is done in a browser: connect the
GitHub repository, and it builds and deploys from the `Dockerfile` on every push. Azure
App Service is the more natural long-term home for a .NET application, but its setup is
considerably heavier, and the container built here runs on Azure, Fly or a plain Linux
server without changes — so nothing is locked in.

Cost is about **$7 a month** for the service, plus a small amount for the 5 GB disk. The
free plan cannot be used: it has no persistent disk, so every photograph uploaded would
be lost on the next deploy.

## Before you start

You need:

- a **GitHub account** with access to the repository
- a **Render account** — sign up at [render.com](https://render.com) with that GitHub
  account
- a **password you have chosen** for the MSM owner login, at least 10 characters with an
  uppercase letter, a lowercase letter, a digit and a symbol

## 1. Create the service

1. In Render, click **New** → **Blueprint**.
2. Choose the `ManchesterSchoolOfModelling` repository.
3. Set the branch to `claude/test-html-page-lqqflh`.
4. Render reads `render.yaml` and shows one service, `msm-portfolio-preview`. Click
   **Apply**.

It will ask for the three values that are deliberately not in the repository:

| Setting | What to enter |
| ------- | ------------- |
| `Seed__SuperAdmin__Email` | The email MSM will sign in with |
| `Seed__SuperAdmin__Password` | The password you chose |
| `Msm__PublicDomain` | Leave blank for now — step 3 |

The first build takes around five minutes.

## 2. Check it works

Render gives the service an address like `https://msm-portfolio-preview.onrender.com`.
Open it and you should land on the Model Board with two models on it.

Sign in at `/account/login` with the email and password from step 1.

### What is already in it

Six invented clients are created on first start, one at each stage of the workflow, so
every screen has something to look at rather than an empty list:

| Client | Stage | What it demonstrates |
| ------ | ----- | -------------------- |
| Amara Whitfield | Published | A live portfolio and a Model Board card |
| Tobias Fenwick | Published | A second board card, and a male measurement template |
| Priya Raval | Ready for review | An Admin's review queue |
| Callum Reid | Retouching | A retoucher's workspace, part-way through |
| Niamh O'Connell | Just onboarded | Unclaimed work waiting to be picked up |
| Elsie Hartley | Retouching, under 18 | Guardian approval outstanding, and publication blocked because of it |

There is also a second sign-in, `retoucher@msm.local`, using the **same password** you
set for the owner. Use it to see the retoucher's view of the queue, which is narrower
than an Admin's.

These are invented people. Real client details do not belong here — see
[What this preview is not](#what-this-preview-is-not).

## 3. Tell the application its own address

Copy the address Render gave you into the `Msm__PublicDomain` environment variable
(**Environment** in the service's settings), with no trailing slash, then redeploy.

This matters more than it looks: every shared portfolio link, every social preview card
and **every guardian approval link** is built from this value. Until it is set, those
links point somewhere wrong.

## 4. The real subdomain, when you want it

In Render, **Settings** → **Custom Domain** → add:

```
model-portfolio.manchesterschoolofmodelling.co.uk
```

Render shows a `CNAME` record. Whoever manages
`manchesterschoolofmodelling.co.uk` adds it at the domain registrar. Once it resolves,
Render issues the certificate automatically.

Then update `Msm__PublicDomain` to
`https://model-portfolio.manchesterschoolofmodelling.co.uk` and redeploy.

Consider whether the preview should use the real subdomain at all. Anything shown there
is associated with MSM's brand, and the site is configured to ask search engines to
ignore it precisely because the models on it are invented.

## What this preview is not

It runs in **Development** mode. That is what makes a demonstration possible, and it is
exactly what must not be true of the live system:

- **Payments take no money.** The placeholder provider authorises every checkout, so the
  purchase journey can be shown end to end. Nothing reaches a bank.
- **Emails are not delivered.** Guardian approval requests are written to the log. An
  under-18 client cannot actually be approved by their guardian here.
- **Detailed error pages are shown.** If something breaks, the page displays internal
  detail. Do not leave a preview running indefinitely on a public address.
- **Uploads are not scanned** for malware (specification section 38).
- **The database and photographs sit on one disk** attached to a single instance.

**Do not put a real client's details or photographs in it.** Use invented names. The
four go-live blockers in the README are unchanged by this preview existing.

## Turning it off

**Settings** → **Suspend** stops the service and the billing without deleting anything.
**Delete** removes the service and the disk, photographs included.
