# Suggested biographies

**It happens by itself.** Open a client who has no biography and one is written for them
in the background; it appears in the **About me** box, ready to read. Change anything that
is wrong and press "Save client details" — that is what accepts it, and nothing is
published until you do.

There are two other ways in, for when you want one on demand:

- **"Write a different one"**, under the About me box, replaces what is there. Use it as
  often as you like. If you have typed something yourself it asks before overwriting.
- **At approval** — "Mark ready for viewing" also asks for one, for any client who somehow
  has not had one written yet.

All three send the same facts and follow the same rule: a person reads it before it goes
anywhere.

## Nothing happens at all?

The most likely reason by far is that **no API key is configured**. Without one the whole
feature is off: no biography is written, automatically or otherwise. The About me box says
so in place of the button. See *Turning it on* below.

## Why it is a draft and not the biography

The text describes a real person, often a young one, and is what an agency reads about
them before deciding whether to book them. A language model given a height and a town
will write fluent sentences about ambition, experience and character that nothing in the
studio's records supports. Those sentences are not a style problem — they are false
statements about somebody's working life, published under the school's name.

So the suggestion lands on the client page for an administrator to read, edit and accept.
Nothing reaches the public portfolio until they do.

## What is sent

Only facts the studio already recorded:

- public name, town, age, and profile category
- the measurements on the client's record, with their labels
- how many photographs are on the portfolio, and whether a self-tape exists

**No photographs are sent.** A biography is written from what the studio knows, and
sending a young person's pictures to a third party to have their appearance described is
not something this feature needs in order to work.

Deliberately absent: email address, telephone number, date of birth (an age is sent
instead), address, guardian details, and the CRM identifier. The request is built from a
small explicit record rather than the client row, so a field added later cannot leak into
it by accident.

## The rules

These govern the biography written for you. The button is an explicit press, so it has
none of them except the last: it can be used any time, on anyone, as often as you like.

- **Once.** Written once per client — on the first visit to their page, or on approval,
  whichever comes first. Visiting again does not write another. Approving again, or sending a portfolio
  back to the retoucher and approving it a second time, does not produce another.
- **Never over the top of a person.** A client who already has a biography is skipped
  entirely — that text was written or approved by somebody.
- **Never published by itself.** It arrives in the About me box and becomes the
  biography when somebody presses Save, having had the chance to change it.
- **Thrown away means finished.** Discarding a draft closes it; another is not offered.
- **Off by default.** With no API key, no draft is ever requested — not queued, not
  failed, not anything.

## Turning it on

Set the key outside source control — user secrets locally, an environment variable in
production:

```bash
dotnet user-secrets set "Biography:ApiKey" "sk-ant-..." --project src/Msm.Portfolio.Web
```

```bash
Biography__ApiKey=sk-ant-...
```

Other settings, all optional:

| Setting | Default | What it does |
|---|---|---|
| `Biography:Model` | `claude-opus-5` | Which model writes the draft. |
| `Biography:TargetWords` | `90` | Roughly how long. Editorial decision, so it is a setting. |
| `Biography:MaxAttempts` | `3` | How many times a failure is retried before it is left alone. |

## When it goes wrong

Writing happens on a background worker, never inside the approval, so a provider that is
slow or down cannot delay or fail an administrator's approval. A failure is retried with a
widening delay held on the client's row — a restart during an outage does not reset it and
hammer a service already struggling. After `MaxAttempts` it gives up, and the client page
says so with the reason. Writing the biography by hand was always the fallback, and it
stays available throughout.

## What is not verified

The request shape is exercised against the live API — an incorrect key is rejected with a
401, which is the API answering, and that rejection is what the button reports on screen —
but **no biography has been generated end to end from this repository**, because no API
key was available when the feature was built. Before
relying on it, turn it on for one client and read what comes back. Check in particular
that nothing in the text is invented: a credit, an agency, a brand, an ambition, or any
claim about experience.
