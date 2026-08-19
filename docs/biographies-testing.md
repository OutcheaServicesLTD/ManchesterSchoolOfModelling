# Turning biographies on, and checking they work

Five steps. You need a card, and about ten minutes.

## 1. Get a key

Go to **console.anthropic.com** and sign up. Then:

- Click **Billing** and add a payment card. There is no free tier, so nothing works
  until you do. Add a small amount to start with — £5 is plenty for hundreds of
  biographies.
- Click **API keys**, then **Create key**. Name it *MSM website*.
- Copy the key. It starts `sk-ant-`. **Copy it now** — the site will not show it to you
  again, and you will have to make a new one.

Paste it somewhere safe for a minute. It is a password: anyone who has it can spend
your money, so it does not go in an email or a message.

## 2. Put the key on the website

The website is hosted on Render.

- Go to **render.com** and sign in.
- Click the **msm-portfolio-preview** service.
- Click **Environment** in the left menu.
- Click **Add Environment Variable**.
- In the first box (**Key**) type exactly: `Biography__ApiKey`
- In the second box (**Value**) paste your key.
- Click **Save Changes**.

**Two underscores** between `Biography` and `ApiKey`, not one. That is the single
easiest thing to get wrong.

The site restarts itself, which takes a couple of minutes. Wait for it to say **Live**.

## 3. Check it is switched on

Sign in to the website as an administrator and click **Integrations** in the top menu.

Find the **Biographies** box.

- **"Switched on"** in green — the key is working. Go to step 4.
- **"Off — none are being written"** — the key did not arrive. Go back to step 2 and
  check the spelling of `Biography__ApiKey`, including the two underscores.

## 4. Try it on one client

Click **Clients** and open somebody whose **About me** box is empty.

The first time you open them, nothing appears yet — the biography is being written. The
page says so. **Wait a minute and refresh the page.**

The About me box should now have a biography in it, with **"This was written for you"**
underneath.

## 5. Read it before you keep it

This is the step that matters. Read every sentence and ask: **do we actually know this?**

It has only been told the name, town, age, category, measurements, how many photographs
there are, and whether there is a self-tape. Anything beyond that it should not have
said. Look especially for:

- named brands, agencies, magazines or campaigns
- claims about experience, training or awards
- opinions about their personality or ambitions

If any of that appears, delete it. If it keeps happening, tell me and I will tighten the
instructions.

When you are happy: change anything you want, then press **Save client details**. That is
what saves it. Nothing is published until you do.

Not happy at all? Press **Write a different one** for a fresh attempt.

## What it costs

Pennies. A biography is a small amount of text, and one is written per client — not per
visit to their page. Hundreds of models cost a few pounds. Your spending is visible under
**Billing** at console.anthropic.com, and you can set a monthly cap there.

## If something goes wrong

- **The Integrations page says "Off"** — the key did not arrive. Check step 2.
- **The About me box stays empty and the Integrations page shows a number next to "gave
  up"** — the key arrived but was refused. Most likely it was pasted with a piece missing,
  or there is no billing set up on the Anthropic account. Make a new key and redo step 2.
- **It writes something untrue** — do not save it. Tell me what it said.
