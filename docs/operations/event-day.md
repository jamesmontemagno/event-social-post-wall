# Run an event

## Rehearsal checklist

- [ ] Confirm the environment/database is the intended one and demo mode is correct.
- [ ] Verify all three HTTPS sites and role-specific packages.
- [ ] Sign in as owner; invite named moderators instead of sharing the owner passcode.
- [ ] Configure the real question URL and your event name/hashtag.
- [ ] Open intake, submit from an attendee device, approve, and refresh the host wall.
- [ ] Test each enabled provider with a new post, image/profile, and deletion/withdrawal.
- [ ] Check quotas, X budget, source health, logs, SQL utilization, and backup recovery.
- [ ] Test the actual venue network: NAT can make attendees share an IP-based limit.
- [ ] Rehearse projector resolution, light/dark mode, immersive mode, and keyboard navigation.
- [ ] Prepare manual entries and an offline/failure plan.

## Owner and moderators

The bootstrap owner signs in as `admin` with `STAFF_PASSCODE`.
Owners manage team access, source settings, intake, filters, manual entry, reports,
and exports. Moderators can review, approve/reject/withdraw, block verified social
accounts, and manage promotional cards, but cannot access owner-only APIs.

In **Team**, invite a moderator using a unique permanent username. Usernames are
case-insensitive, 3-40 ASCII letters/digits/dots/hyphens/underscores, starting with
a letter/digit. `admin` is reserved.

Invitation codes are shown only on create/rotate. Share them privately; never
put codes in URLs or screenshots. Only a salted hash is stored.

- **Rotate code** invalidates the old code and the member's sessions.
- **Disable access** blocks login and existing sessions without deleting history.
- **Invite again** restores access with a new code.
- Rotating the owner passcode invalidates staff sessions; moderator codes remain
  valid until separately rotated/disabled.

**Online recently** means a console checked in, not that someone is actively
reviewing. Heartbeats run every three minutes; presence expires after five minutes
and resets on restart. A failed refresh displays an unavailable state.

## Review the queue

New content is Pending or automatically Rejected. Review before selecting
**Approve & send live**. Inspect photos in the attachment preview without
publishing them. List/Grid views, search, source/time attribution, and rejection
reasons help triage a busy queue.

**Withdraw** removes a live item. The wall checks roughly every minute and on tab
visibility, so removal is not instantaneous. During an outage, a wall keeps its
last snapshot until the next successful check. If urgent removal cannot wait,
switch the projection away from the wall.

Versions detect conflicting staff actions. On a conflict, reload/review the
current state rather than repeatedly submitting an old decision.

## Filters and author blocks

Configurable filters cover profanity, all-caps messages, single words, emoji-only
or link-only content, short location check-ins, repeated-character spam, and exact
duplicate text. Staff can add blocked terms, allowed profanity terms, and location
phrases. Lists accept up to 25 entries of 40 characters each.

These are conservative heuristics, not comprehensive language detection.
False positives remain reviewable and can be explicitly approved. Settings apply
to future arrivals/material edits, not retroactively to the whole queue. Passing
filters never means automatic publication.

Author blocking uses verified provider account identifiers, not display names.
Anonymous questions cannot be reliably grouped into a person by a supplied name.

## Host presentation

- The wall loads the latest 100 posts in its selected sort; refresh may include
  up to 100 additional newly approved posts.
- Refresh/Show new posts is manual so the stage view stays predictable.
- Sort by post time or approval time. Pin locally and expand a post for reading.
- Pins, style, theme, and promo visibility are per-browser/per-event preferences.
- Immersive mode hides navigation; controls appear on hover/focus and stay usable
  on touch. Escape exits fullscreen. A denied fullscreen request still permits a
  browser-filling wall.
- `t.co` shortlinks are hidden in wall text only; original text remains available
  in staff review/exports. Original-post links and images are retained.
- No question form is embedded in the wall: attendees use the separate linked site.

## Promotional cards

Owners/moderators create promos with a title (1-80 characters), description
(1-500), and optional absolute HTTP(S) URL (up to 2,048 characters, no credentials).
Enable **Active on wall** to make a promo available publicly.

Hosts use the star control to show/hide active promos locally. Cards appear below
pins; selecting one opens its description/link and a locally generated QR code.
No external QR service is called. A URL too complex to encode keeps a readable
link with an explicit QR warning. Deactivation/deletion propagates through normal
wall change checks.

## Reports and exports

The owner-only **Event report** loads on Generate/Refresh, not automatically.
It covers stored moderation/status counts, source comparisons, timelines, decision
latency, queue age, blocks, and source health. Print/Save PDF uses the browser.
CSV/JSON exports include the whole event under provider content-export gates,
not only the current report filter selection.

Report filters use receipt time and local calendar dates. Historical categories
can overlap; current approval rate is Live / (Live + Rejected), not the percentage
ever approved. Decision latency uses the first recorded human decision. Reports
do not infer reach, impressions, unstored visits, or whether a host read a post aloud.

## After the event

Pause intake, stop YouTube, disable other collectors, review provider retention
obligations, revoke temporary team access, and capture approved exports/backups.
Stopping an app does not stop App Service plan or SQL charges. Delete resources
only after confirming backups, retention requirements, and ownership of the exact
resource group.
