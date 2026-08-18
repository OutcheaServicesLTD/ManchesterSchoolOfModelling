# Suggested biographies

Two ways in, both ending with a person deciding.

**The button.** On a client's page, under the **About me** box, "Write one for me" writes
a biography and puts it in the box. Nothing is stored — it is text in a form until "Save
client details" is pressed. Press it as many times as you like. If the box already has
something in it, it asks before replacing it.

**At approval.** When an administrator approves a portfolio — "Mark ready for viewing" —
one is written in the background and offered on the client page as a draft, once, to be
accepted or thrown away.

Both send the same facts and follow the same rule: a person reads it before it goes
anywhere.

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

These govern the automatic draft at approval. The button is an explicit press, so it has
none of them except the last: it can be used any time, on anyone, as often as you like.

- **Once.** Requested only on the first approval. Approving again, or sending a portfolio
  back to the retoucher and approving it a second time, does not produce another.
- **Never over the top of a person.** A client who already has a biography is skipped
  entirely — that text was written or approved by somebody.
- **Never automatic.** A draft becomes the biography when an administrator presses "Use
  this biography", and is still editable afterwards.
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
