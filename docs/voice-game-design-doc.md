# voice training game — design doc & roadmap v2.3

companion to `voice-analysis-spec.md`, which covers phase 0. this doc picks up after the analysis prototype clears its decision gate.

v2 incorporates a design review plus four direction decisions (hybrid chart targets, friends-only social, rating exchange cut to research note, resonance proxy fallback). v2.2 applies the second Sol review: phase 0 gate split, a normative scoring appendix, curriculum matched to shipped mechanics, goal ownership fixed, replay privacy mechanics specified, and the perception model rescoped. v2.3 (paired with analyzer spec v2.3) adds:

- a resonance specificity gate
- confidence floors with a defined meaning
- a precise capture-to-result threshold
- reference tones as an open decision
- cross-reference fixes

changes are summarized in part 5.

---

# part 1 — design decisions

these are settled. everything in the roadmap assumes them.

## 1.1 the core principle

**score the performance, never the person.**

every number the game shows is about *how well you executed this exercise, tonight*. no number is about how feminine, masculine, or passing your voice is. this isn't a hedge — it's what makes the retry loop work. score-chasing depends on failure being cheap, and a bad run has to mean "bad run," not "you don't pass."

the corollary: the *targets* come from the curriculum and from goals the user sets (alone, or with their coach). the game scores how close they got. it never decides what they should be aiming for.

## 1.2 the four-surface architecture

| surface | what it measures | timescale | comparable? |
|---|---|---|---|
| **chart grade** | execution accuracy on one run | seconds | yes — scored charts are the same for everyone |
| **collection score** | mastery across the scored library | days–weeks | yes, friends-only opt-in |
| **progress trends** | the voice itself, over time | months | no — personal only |
| **voice profile** | where each attribute sits now | current state | no — multi-axis by design |

### the motivation model — a solo journey

**this is a solo journey first.** every primary reward loop closes without any other player existing. friends comparison is present and encouraged (§ collection score), but it's seasoning — nothing in the core loop requires it, and no reward is locked behind it. the game must be fully motivating for a user who never opts into anything social, because many in this audience won't.

the engagement engine is layered so there's a dopamine hit at every timescale:

- **in-run**: frame judgments, combo counter, live score ticker — the moment-to-moment juice.
- **end-of-run**: grade reveal, and above all the **personal best delta**. every chart tracks PB score, PB grade, and best-combo status; every run ends with `+12,430 vs your best` or `closest yet — 3,180 from S`. beating a PB is the loudest celebration in the game, louder than any letter threshold — grades are fixed bars, but a new best is *yours*.
- **near-miss surfacing**: "one Great away from Full Combo," "2% from A." the retry impulse is the core loop; feed it explicitly.
- **per-session**: a session summary — charts improved, PBs set, minutes practiced, streak progress. ending on a summary screen that shows *something* moved is what turns tonight's practice into tomorrow's.
- **long-arc**: mastery ladder (S → SS → SSS per chart), campaign unlocks, achievements, cosmetics keyed to consistency, collection score milestones, and trend milestones from the progress screen ("steadiest hold yet," "widest comfortable range yet").

**guardrail:** the dopamine machine obeys §1.9 without exception. every mechanic above rewards *execution quality and showing up* — none rewards pushing higher, louder, or longer. PBs are per-chart execution scores, so chasing them can't drag a user out of their safe range; streaks are rest-inclusive; the session soft-stop outranks any near-miss prompt (past the soft stop, the game stops suggesting "one more try").

### chart targets — hybrid (new in v2)

v1 claimed collection score was fair because "everyone plays the same charts," while also mapping the highway to the user's assessed range and forbidding charts outside the safe range. those can't all be true. resolution:

- **scored library (collection charts): absolute targets**, deliberately authored inside a band nearly every post-warmup voice can reach safely. these are identical for everyone — that's what makes grades and collection score comparable, and it keeps the promise that someone with the hardest starting voice can still 100% the game.
- **campaign and free practice: personalized targets**, placed relative to your assessed range and goals. always safe, always relevant, graded for feedback but not counted in collection score.
- the safe-range gate (§1.9) applies to both. a scored chart that a user's current safe range can't reach yet shows as "not yet in range" with the assessment path to unlock it — never as a failure.
- **the universal band is a hypothesis, not a fact** (v2.2). it gets tested against the phase 0 corpus's range data plus SLP input, and it may fail — there may be no pitch+resonance band that is simultaneously safe, reachable, and worth practicing for the whole audience. defined fallback if so: collection score switches to an **eligible-chart denominator** — mastery % computed over the charts within each player's assessed range, denominator always visible ("S-ranked 34 of your 52 eligible") — and friends comparison additionally surfaces the identical-chart subset both players can access. that degrades comparability honestly instead of quietly breaking the 100%-for-everyone promise.

### chart grade — the engagement loop

per-exercise, per-run. **S / A / B / C / D**, plus a numeric score. resets every attempt.

**judgment windows must be wider than tracker noise.** phase 0's accuracy gate is FPE < 15 cents RMSE — v1's `Perfect < 15 cents` sat exactly on the tracker's noise floor, so a user singing perfectly would still draw random Greats. defaults (per-chart configurable, all ≥ 2× measured tracker error):

```
per-frame error from target (cents for pitch; per-dimension unit otherwise)
  Perfect  < 30 cents
  Great    < 60
  Good     < 100
  Miss     ≥ 100, or unvoiced/creak when the chart expects voice

frame policy (new in v2 — this is what makes grading feel fair):
  - a Miss SEGMENT requires ≥5 consecutive miss frames (50 ms).
    single-frame blips from voicing flicker or octave-correction settling
    do not count against anyone.
  - onset grace: the first 150 ms after a chart segment begins, and after
    any marked breath point, is unscored.
  - creak while voice is expected: scores as miss frames, but the UI shows
    a creak indicator, not a generic miss — the user should learn what
    happened, not just that it "missed."
  - unreliable frames are excluded from scoring, per dimension. if a
    run has too many of them, it isn't graded on that dimension. never
    grade what the tracker couldn't actually see — a wrong grade costs
    trust that honest abstention doesn't.
    "unreliable" (v2.3) depends on which confidence answers the question
    being scored (analyzer spec §3.9). each is a calibrated probability
    checked against a per-chart floor, default 0.9:
      - voicing state (is there voice, creak, or nothing?):
        VoicingConfidence. a confidently detected Creak or Unvoiced frame
        is a Miss, not an abstention.
      - pitch on a Voiced frame: F0Confidence. 0.9 means "≤ 10% chance
        this frame's pitch is a gross error." F0Confidence doesn't exist
        for non-Voiced frames and is never consulted for them.
      - resonance on a Voiced frame: its own validity and confidence
        rules. high pitch confidence says nothing about F2.
    the full per-dimension table is in appendix A. the floors trade
    wrong grades (too low) against ungraded runs (too high). the
    analyzer's coverage-at-floor metric measures the second, and the
    defaults get tuned at Gate A, especially on breathy phonation.

chart score = weighted mean of frame accuracy × completion, scaled to 1,000,000
  (per dimension: completion = fraction of scoreable chart time with a
  judged frame on that dimension; see appendix A)

letter thresholds:  D 50%  |  C 65%  |  B 78%  |  A 88%  |  S 95%
Full Combo = zero Miss segments
```

for multi-dimension charts (pitch + resonance), score each dimension separately then combine as a **weighted mean with a floor** — you must not be able to S-rank by nailing pitch and ignoring resonance.

**tier names stay semantically empty.** S, SS, SSS, AAA. never "Passing," never "Feminine," never "Natural." the moment a tier has semantic content about gender, the whole problem comes back through the copy.

### the replay loop (new in v2 — this is core, not a feature)

the game's premise is repetition **and listening back**. every graded run is auto-recorded locally, with mechanics that keep this compatible with the analyzer spec's privacy rules (specified in v2.2 — v2 asserted compatibility without defining it):

- **ephemeral means ephemeral**: session recordings live in memory (or an encrypted temp file if memory pressure demands it), are purged on session end, and a startup sweep deletes any temp audio a crash left behind. ephemeral audio never counts as "retained."
- **pinning is the consent moment**: pinning a run is the explicit per-recording act that persists audio, with a visible "recorded & kept" indicator on pinned items and one-click plus bulk deletion. onboarding states the session-buffer behavior plainly before the first exercise.

- **instant replay**: hear the run with the trace and judgments overlaid. one key/button, zero friction — the loop is *attempt → listen → adjust → retry*, and the listen step is where the actual learning happens.
- **A/B compare**: play a pinned earlier attempt against tonight's, same chart, traces overlaid. hearing your own three-weeks-ago voice is the single most convincing progress evidence this product can offer — more than any chart or trend line.
- replay is also the self-monitoring skill SLPs actually teach: the gap between what a voice feels like and what it sounds like is the central obstacle in voice training. the game should say so in the tutorial and build the habit mechanically.

### collection score — the meta number

`"S-ranked 34 of 60 exercises"` / `"full-comboed the Resonance campaign"` / a mastery percentage across the scored library.

**first and foremost this is a solo completion meter** — the long-arc number *you* are filling in, the top of the personal-best pyramid (§ motivation model). comparison is **friends-only and opt-in** (new in v2): compare collection score and clears with Steam friends who have also opted in — present and encouraged, but secondary by design. no global leaderboards at launch — a trans-focused title does not hand strangers a ranked list of its players. revisit post-launch only with moderation actually in place.

it works as the meta number because:

- huge range, effectively no ties
- fair — the scored library is identical for everyone (§ chart targets)
- moves on a timescale of days, not years
- someone with the hardest starting voice can still 100% it
- cannot be read as a verdict on whether they pass

### progress trends — retention

separate screen. personal, never compared, **no letter grades and no failure pole.**

median speaking f0 over 30 days, resonance estimate trend, weight (once tracked), intonation range (semitone IQR over read passages). sparklines. `"up 18 Hz since March."` plus pinned recordings as audio waypoints (§ replay loop).

this is where most skill apps fail — they can't show slow progress on a slow skill, so users conclude nothing is happening and leave. precise measurement is your advantage here; use it.

### voice profile — the honest "where am I"

multi-axis display: pitch, resonance, weight, intonation, each shown against **the user's own goal bands**, set during onboarding and editable anytime. radar chart, stat page, whatever fits the art direction.

- reference overlays (population ranges from the literature) are **optional and off by default**, framed as ranges with heavy overlap — never as two poles. this is also how non-binary and androgynous goals work natively rather than as a patch: the axes are ranges you place yourself on, not a slider between two ends.
- deliberately **not collapsed to a scalar**, which means it structurally cannot be leaderboarded. that's the feature.

## 1.3 the passing question

users will want to know whether they pass. the honest answer is that no acoustic model can tell them — perceived gender lives in listeners, context, and expectation, and any in-game number claiming otherwise would be the most-trusted lie in the product.

so the game's stance, stated plainly in the UI where the question naturally arises:

- the profile and trends show *what the voice is doing* against *the user's own goals*
- the replay loop gives them their own ears, which adapt slower than their self-perception distorts
- if they want human perceptual feedback, the game points outward — coaches, communities they already trust — rather than simulating it

**the blind community rating exchange from v1 is cut from the roadmap** (decision, 2026-08). it was the most legally and moderation-expensive feature in the doc, everything else works without it, and shipping it half-resourced is worse than not shipping it. it survives as a research note in part 4 in case it's ever revisited with real funding for moderation, abuse design, and counsel. the perception model (§1.11) no longer assumes its data exists.

## 1.4 what gets measured

| tier | attributes | in scope |
|---|---|---|
| 1 | f0 (median, stability, glide accuracy) and **resonance** (§1.5) | phase 1 |
| 2 | vocal weight (spectral tilt, CPP; H1–H2 once analyzer phase 1 validates it), intonation range and contour | phase 2 |
| 3 | breathiness, /s/ centroid, rate, articulation precision | not gamified |

**resonance is not optional in the MVP.** it's roughly co-equal with pitch in the literature on perceived gender, and shipping a "voice training game" that only tracks pitch would be a legitimate criticism.

**weight has an analyzer dependency** (new in v2): jitter/shimmer and H1–H2 were cut from analyzer phase 0 as invalidly defined without pulse-synchronous analysis; CPP survived as a diagnostic. the phase 2 weight dimension therefore depends on analyzer phase 1 work (pulse extraction, corrected H1–H2) clearing its own validation before weight becomes a *scored* dimension. spectral tilt + CPP may carry an unscored "weight meter" earlier. (v2.3: the analyzer now publishes `SpectralTiltDbPerKhz` as an experimental field, analyzer spec §3.7b.)

**breathiness stays out of scored mechanics.** it's widely taught as a feminization lever but the evidence that it moves gender perception is genuinely mixed. teach it if the SLP says to; don't build a rank around it.

**volume is not weight.** RMS measures loudness. weight is fold mass, and it shows up in spectral tilt and CPP. if the tutorial teaches weight and the game scores loudness, learners optimize for the wrong thing and can't articulate why they're stuck.

## 1.5 resonance: measured truth vs gameplay signal (new in v2)

live per-frame LPC formant tracking is the least proven part of the analysis chain, and analyzer v2 already rescoped VTL to a batch-only experimental aggregate. the game must not bet its co-equal-with-pitch dimension on the shakiest tracker output. so, two layers:

- **measured truth**: formants (F1–F4) from the analyzer, used for the voice profile, progress trends, and batch analysis. computed on fixed-vowel exercises where they're actually comparable. caveat (v2.3): LPC formants get less reliable as f0 rises (analyzer spec §3.6), and that's the direction many users are heading. trend and profile displays must carry the formant confidence, so an apparent resonance change isn't really a pitch change showing through a degrading measurement.
- **gameplay signal**: the resonance lane is driven by whichever signal *passes validation on the phase 0 corpus*:
  - primary candidate: tracked F2 (or formant dispersion) on fixed-vowel charts, smoothed for display
  - fallback: a simpler spectral-brightness proxy (e.g., energy balance between low and mid bands, or spectral centroid over 0–4 kHz) computed on the same fixed vowel, validated *against* formant measurements offline so it demonstrably moves when resonance moves
  - validation gate (tightened in v2.2 — this is real work, not a freebie): the phase 0 corpus gains a dedicated **resonance-manipulation task** (same speaker, same vowel, same pitch target, contrasted tract postures, repeated across devices and sessions — now specified in analyzer spec §6), and both candidate signals are scored on it for **direction accuracy** (does the signal move the right way when the tract changes), **test–retest stability**, **cross-device sensitivity**, and (v2.3) **specificity**. the gameplay signal is whichever passes. if neither does, that's a phase 0 red flag on the resonance lane itself, surfaced at Gate B (part 2, phase 0) — not discovered in phase 1
  - why specificity is a safety criterion (v2.3): brightness measures rise when spectral tilt flattens, and tilt flattens when you push harder or phonate heavier. an unchecked proxy would score a user *better* for pressing, which is a strain incentive that §1.9 forbids. it would also score lighter phonation, a common goal, as *darker*. so the signal must move more for a posture contrast than for pitch, loudness, or weight changes at a fixed posture. the analyzer spec's cross-talk corpus tasks measure this (§3.7b, §6), and a tilt-normalized proxy variant is scored alongside the raw one

the MVP ships either way. the UI never claims more precision than the signal carries; if the proxy ships, the profile still reports real formants from batch analysis.

## 1.6 the fixed-vowel rule

**every exercise specifies its vowel.** this is free — the game tells the user what to say anyway — and it's what makes F1/F2 (and the brightness proxy) comparable across sessions and across users. without it, a formant meter is mostly a vowel detector.

tag every logged frame with the expected vowel so batch analysis can group correctly. as a light guard, batch analysis sanity-checks produced formants against the expected vowel's neighborhood; a mismatch flags the run "vowel mismatch — not scored on resonance" as feedback, never as a miss.

## 1.7 tracing, not hitting

the analysis chain has a hard latency floor — YIN needs 2–3 periods of audio before any answer exists, and the honest end-to-end number (analyzer spec §5) is **~50–80 ms steady-state and ~70–110 ms on transitions**, user-to-photon, not v1's optimistic ~40 ms. it cannot be engineered away.

so: **sustained lines, glides, and contours.** no tight timing windows, no discrete note-hits with millisecond judgment. the highway is something you trace, not something you strike. scoring compensates for the measured latency offset (calibration parameter) so the trace is *judged* against where the voice actually was, even though it *renders* late.

## 1.8 the highway

- **log-frequency vertical axis**, mapped to the user's assessed range rather than absolute Hz
- **Hz labels visible** — the community talks in Hz and will want the numbers
- second tracked dimension (resonance) encoded redundantly — **colour plus at least one non-colour channel** (thickness, glow, a paired ribbon). this was already required by the colourblind-safe accessibility rule; make it a design constraint from the first mockup, not a retrofit
- neon glow via Godot's HDR 2D bloom — push emission above 1.0 rather than faking it in shader
- display smoothing (the analyzer's `F0DisplayHz`: causal median + slew limit) is separate from scoring, which uses raw published frames

## 1.9 safety

vocal strain is a real injury risk and this is the part that must not be an afterthought.

- **range gating.** the baseline assessment sets a safe ceiling and floor. the game never generates a chart that pushes past it. the ceiling expands only as the assessment moves.
- **the assessment protocol is an SLP deliverable and a phase 1 blocker** (v2.2). how floor and ceiling are derived, the safety margin applied, what counts as "higher-intensity," symptom screening ("does anything hurt?" is a protocol step, not UI copy), and reassessment cadence are written and signed off by the SLP *before* 1a is implemented — not discovered in the phase 2 playtest. until that protocol exists, "safe range" is a label, not a safety feature.
- **warmups** required before higher-intensity exercises
- **session length limits** with a soft stop, and a visible "stop if it hurts" convention that appears in the exercise UI, not just in a disclaimer nobody reads
- progression keys off **practice consistency and chart execution**, never off pushing higher or louder
- **rest days count** (new in v2). any streak or consistency mechanic must not punish vocal rest — rest is part of the practice protocol, and a streak that breaks on a rest day is a mechanic that pays users to injure themselves. streaks accrue on a "practiced or rested deliberately" basis, and scored high-intensity practice has a soft daily cap.

## 1.10 privacy

- **all analysis is local.** no audio leaves the machine by default. run recordings (§ replay loop) are local, session-ephemeral unless pinned, and bulk-deletable.
- no audio, frames, or derived voice measurements in telemetry or crash reports (inherited from analyzer spec §0 — same rule, same wording).
- say all of this plainly on the store page — for this audience it's a purchase decision, not fine print.
- voice is treated as biometric data under Illinois BIPA, Texas CUBI, and Washington state law. get counsel before any feature that transmits or stores audio server-side. (with the rating exchange cut, *no current roadmap feature transmits audio.* keep it that way unless something earns the legal spend.)

## 1.11 goal-band analytics now; perception model off the roadmap (rescoped in v2.2)

v2 carried a contradiction: §1.3 says the game points outward for perceptual feedback rather than simulating it, and this section then proposed a model reporting how listeners tend to hear the acoustics. no amount of intervals and careful copy stops that output from becoming the passing score the entire architecture exists to avoid. resolution, in two parts:

- **the useful feature never needed a perception model.** *"pitch is in range, resonance is your limiting factor"* is goal-band arithmetic — the distance of each tracked dimension from the *user's own* goal bands, ranked. that ships as **goal-band analytics** (phase 2, with the profile screens): fully interpretable, zero perceptual claims, no training data, consistent with §1.3. this is the diagnostic a teaching tool actually needs.
- **the perceptual model is demoted from roadmap item to explicitly open product decision.** the bar for ever reopening it, written down now: a consented, perceptually-rated corpus (university-lab partnership under IRB, or commissioned paid raters — never identity labels scraped from platforms: wrong construct, wrong ethics; Mozilla Common Voice usable for pretraining only), distribution-with-interval output, and — hardest — a convincing answer for why it wouldn't just function as a passing score in a hat. absent that answer, it stays out, and nothing in the product depends on it.

## 1.12 commercial

- **one-time purchase.** the incumbents are subscription mobile apps; this is a real differentiator and it matters for an audience that skews financially precarious.
- generous regional pricing
- consider a gifting or need-based path
- Steam Deck as an explicit target — heavy overlap with the audience, and the Deck has a usable built-in mic. **add the Deck mic as a corpus condition in phase 0** so "usable" is a measured claim before it's a store-page claim.

## 1.13 community

a trans-focused title on Steam will attract exactly what you'd expect. decide before launch, not during:

- Steam discussion board disabled or heavily moderated, with a moderated Discord as the real venue
- a written review-bomb response plan
- friends-only social features (§ collection score) keep the in-game abuse surface near zero at launch; anything larger waits for real moderation capacity

---

# part 2 — roadmap

## phase 0 — analysis prototype ✅ spec'd separately

phase 0 exits through **two gates** (v2.2 — the two docs previously disagreed on where the gate sat and what it contained):

**Gate A — analyzer viability**, after analyzer build step 6, exactly as `voice-analysis-spec.md` §8 defines it: GPE < 2%, VDE < 5%, FPE < 15 cents aspirational — gating per slice only where slices meet minimum size (≥3 speakers, ≥10 files), regression gates elsewhere, scored on the **causal live output**. v2.3 adds coverage on the soft/breathy slice at the §1.2 confidence floor: an analyzer that leaves typical breathy runs "tracking unreliable" fails here even if GPE looks fine. if the noisy, soft, and creaky slices won't come down, change approach here. it's cheap now and expensive everywhere after.

**Gate B — product readiness**, after analyzer build step 8 and before any phase 1 work:

- the resonance-signal decision (§1.5): formants or the brightness proxy must pass direction accuracy, test–retest, cross-device, and specificity criteria on the resonance-manipulation and cross-talk corpus tasks
- the two-dimension legibility verdict from the live visualization
- p95 capture-to-result < 10 ms, measured per analyzer spec §3.1 from the arrival of the last sample a frame depends on. that's compute plus scheduling only; the fixed ~21 ms algorithmic delay is reported separately and belongs to the user-to-photon envelope. (v2.3: v2.2 said < 25 ms against an ambiguous definition. measured from the window center, the same frame reads ~21 ms slower, so 25 ms was either trivially met or barely failed depending on the reading.)
- camera-test user-to-photon inside the §1.7 envelope, on target hardware including a Steam Deck.

these thresholds are **product requirements and live in this document**. the analyzer spec measures and reports the numbers but does not gate on them.

## phase 1 — MVP

**goal:** one person can install this, be taught the fundamentals, and practice one exercise repeatedly — with trustworthy feedback and a replay loop they actually use.

**engine:** Godot 4.x with C#. VoiceCore is referenced as a .NET project — unchanged from phase 0, no rewrite.

### 1a — integration and calibration

- **capture path decision** (new in v2): evaluate native in-process capture (PortAudioSharp, as in VoiceProbe) against Godot's `AudioEffectCapture` before committing. `AudioEffectCapture` routes through Godot's audio server (extra buffering, less control over device format, possible OS-processed stream); VoiceCore doesn't care where buffers come from, so use whichever path measures better on latency and rawness. (v2.3: VoiceProbe is now a Godot project, so this comparison starts in phase 0 at analyzer build step 3. 1a confirms the choice rather than starting it.)
- **onboarding flow**: device selection, input gain, noise floor measurement, latency offset (loopback or manual tap-to-sync)
- **baseline voice assessment**: comfortable speaking f0, range floor and ceiling, resonance baseline, one read passage
- assessment output writes **observed range and the safe-range gate only** (v2.2 — measurement, not goals). goal bands are *chosen by the user* in a separate onboarding step: the UI may offer starting suggestions relative to the observed range, but the user places and confirms them, may skip entirely, and **goal-band editing ships in the MVP**. this keeps the analyzer-spec rule intact: goals are user-chosen, editable, and default to nothing.

### 1b — the highway

- log-frequency pitch line with HDR neon glow
- resonance as a second visual dimension, redundantly encoded (§1.8)
- one background theme
- chart format defined and serialized. a chart is: target contour per scored dimension, vowel, duration, per-dimension judgment windows (defaulting from §1.2), breath/grace marks, dimensions scored, per-confidence floors (appendix A), and whether it's a scored-library (absolute) or personalized chart. pitch targets must sit ≥ 100 cents inside the analyzer's search range for that exercise type (v2.3, appendix A)

### 1c — tutorial campaign

6–8 lessons. **an SLP or gender-affirming voice coach writes or reviews the actual content** — this skeleton is a placeholder for structure, not curriculum:

1. how your voice works — anatomy, folds, vocal tract. orientation and safety, no exercise.
2. safety and warmup — the rules, warning signs, when to stop
3. finding your baseline — reading your own assessment, **and learning the replay habit: what you feel isn't what they hear**
4. pitch — what it is, sirens and glides
5. resonance — the big one. tract size, brightness, why pitch alone isn't enough
6. weight — thick vs light, and why it isn't volume. **educational + unscored in MVP** (v2.2): uses the unscored weight meter (§1.4) and replay; no graded chart until the weight dimension clears its analyzer dependency in phase 2
7. putting it together — sustained targets across dimensions
8. into speech — carrying it into a phrase. **ungraded in MVP** (v2.2): free-read with replay and pitch-trace overlay; the graded version *is* Phrase mode and arrives with it in phase 2

each lesson: explanation → guided demo → practice → listen back. practice is a graded Hold chart where the mechanic exists; lessons 6 and 8 practice ungraded in MVP rather than gating on mechanics that don't ship until alpha.

### 1d — one practice mode + grading + replay

**Hold** — sustain a target line at a set pitch and resonance. scores stability and duration. simplest mode, hardest to get right, and it's the one every other mode is built on.

chart grading per §1.2 including miss segments, grace windows, and the unreliable-run flag. **instant replay and pinning ship here, in the MVP** — the replay loop is the pedagogy, not a phase 2 nicety.

**the solo motivation spine also ships here, not in alpha**: per-chart personal bests, the end-of-run PB delta and new-best celebration, near-miss surfacing, and the session summary (§ motivation model). the MVP exit criterion is "wants to replay the Hold charts," and PBs are the mechanic that makes replaying feel like progress rather than repetition. save/progress. local profile.

### 1e — settings and accessibility

- colourblind-safe palettes (resonance is redundantly encoded regardless, §1.8)
- glow/flash intensity slider, and a reduced-motion mode — photosensitivity, and neon-heavy visuals are fatiguing over long practice sessions
- font scaling, full remapping
- an audio-only feedback option

**phase 1 exit:** a stranger can install it, complete onboarding without help, finish the campaign, and want to replay the Hold charts — and has listened back to their own attempts unprompted.

## phase 2 — alpha

**goal:** it's a game, not a demo.

- **three more practice modes** reusing the highway:
  - **Glide** — follow a moving contour. scores tracking error.
  - **Steps** — hit discrete targets and settle. scores settle time and overshoot (settle windows sized to the measured latency, per §1.7).
  - **Phrase** — read a prompt against a target contour overlay. scores contour match and median f0. this is the bridge from exercise to speech.
- **A/B replay compare** (pinned attempt vs tonight) lands here if it didn't fit in MVP
- **weight and intonation** added as tracked dimensions; weight becomes *scored* only once its analyzer dependency clears (§1.4), unscored meter before that
- **collection score, mastery ladder (SS/SSS tiers), achievements, skins** — cosmetics keyed to practice consistency (rest-inclusive, §1.9) and chart clears; all of it solo-closable per the motivation model
- **friends-only opt-in comparison** (§ collection score) — layered on top of, never in place of, the solo loops
- **progress trends** and **voice profile** screens (goal bands already editable since MVP)
- **Steam integration**: achievements, cloud saves (settings and progress only — never audio), rich presence
- **store page live** — wishlists compound, get this up at the start of the phase, not the end
- **closed playtest** with community voice coaches and 20–50 users. this is where the curriculum gets its real critique.

any bucketed metric introduced here needs **hysteresis** — harder to fall than to climb — and a trailing median over many sessions as its input. a composite of five noisy measurements has more variance than any one of them, and tier flicker from a head cold or a headset change is brutal.

## phase 3 — beta / early access

- goal-band analytics mature here (better explanations, per-dimension practice suggestions); the perception model is no longer on the roadmap (§1.11)
- content expansion — more charts, more themes, campaign chapter 2
- Steam Deck verification (mic condition already validated in phase 0)
- Next Fest demo
- localization scoping

## phase 4 — launch and beyond

- perception model decision may be reopened only against the §1.11 bar; nothing else waits on it
- community chart creation?
- FTM/masculinization curriculum as a distinct track — genuinely different pedagogy, not a mirror of the existing one

---

# part 3 — risks

| risk | severity | mitigation |
|---|---|---|
| tracker accuracy insufficient on real hardware | fatal | phase 0 gate; per-slice metrics with slice minimums |
| curriculum lacks credibility with the audience | fatal | SLP review budgeted as a line item, credited on the store page |
| scoring reads as a verdict on passing | severe | §1.1–1.3 architecture; audit every string in the UI for gender semantics |
| someone injures their voice | severe | range gating, warmups, session limits, rest-inclusive streaks |
| grading feels random (tracker noise, voicing flicker) | severe | windows ≥2× tracker error; miss segments not miss frames; unreliable-run abstention |
| review-bombing / harassment | high | §1.13 decided pre-launch; friends-only social at launch |
| live formant lane unreliable | medium | §1.5 two-layer design; validated proxy fallback; MVP ships either way |
| resonance UX illegible as a second dimension | medium | prototype in phase 0 step 8, before committing chart design |
| safe-range protocol unwritten when 1a is built | severe | SLP-authored assessment protocol is a named phase 1a dependency (§1.9) |
| no universal scored band exists | medium | hypothesis tested against phase 0 range data; eligible-denominator fallback pre-defined (§1.2) |
| perception-model pressure returns (users want a passing score) | medium | §1.3 stance; §1.11 reopening bar; goal-band analytics answer the diagnostic need without perceptual claims |
| resonance signal rewards pressed/effortful phonation | severe | specificity gate at Gate B; cross-talk corpus tasks; tilt-normalized proxy variant (§1.5) |
| reference tone through speakers gets tracked and scored as the user | high | open decision (part 4); tone-bleed corpus condition in phase 0 |

---

# part 4 — open questions & research notes

open:

- does the campaign lead with **pitch** or **resonance**? pedagogically defensible both ways, and it changes lesson order and which charts ship first. this is an SLP question.
- **who reviews the curriculum and writes the assessment/safety protocol, and when do you book them?** the protocol is now a phase 1a blocker (§1.9), so this has the longest lead time of anything in the doc. start now.
- exact placement of the scored library's "reachable by nearly everyone" band — needs the phase 0 corpus's range data plus SLP input, not a guess. fallback pre-defined in §1.2 if no such band exists.
- **does the game play reference tones?** (new in v2.3.) hearing the target pitch is standard in pitch training, and nothing in this doc says yes or no. if tones play through laptop or Deck speakers while the mic is live, the analyzer will track the speaker and score it as a perfect run. options:
  - require headphones for charts with a tone
  - detect bleed: the game knows exactly what it played, so a frame whose f0 and level match the reference too well can be flagged
  - play the tone only *before* each segment (a count-in), never during

  this affects chart design (§1.2 breath/grace marks), onboarding, and the phase 0 corpus (analyzer spec §6 has a conditional tone-bleed task). decide before chart format is frozen in 1b.

resolved in v2 (recorded so they stay resolved): hybrid chart targets; friends-only opt-in comparison, no global leaderboards at launch; rating exchange cut from roadmap; resonance proxy fallback accepted; non-binary/androgynous goals handled natively by user-set goal bands with no two-pole visuals (§ voice profile).

### research note: blind community rating exchange (cut from roadmap)

the v1 idea, preserved verbatim in spirit: opt-in, per-clip; anonymized clips rated blind by other users; the user gets back a distribution ("8 of 12 listeners heard this as female"), which *is* perceived gender measured the way the literature measures it, and would quietly generate a rated corpus. cut because it requires funded moderation, abuse design (rate limits, reporting, rater reputation), and biometric-data counsel before it's shippable, and nothing else in the product depends on it. revisit only with all three resourced.

---

# appendix A — normative scoring (new in v2.2)

the §1.2 rules made deterministic. the implementation must match this appendix exactly, and ships with worked test vectors (synthetic frame sequences → expected score) checked in as unit tests. all constants below are defaults, overridable per chart, and versioned (see PB provenance).

- **frame values**: Perfect = 1.0, Great = 0.6, Good = 0.3, Miss = 0.0.
- **miss segments vs miss frames**: miss-quality frames become a *Miss segment* only as part of a ≥5-consecutive-frame run. shorter excursions still score 0.0 per frame, but they do not break Full Combo and are not displayed as Misses — **display and FC track segments; score tracks frames.** no frame is ever double-counted or retroactively rescored.
- **per-frame outcome, per dimension** (v2.3, Astra review). every non-grace frame gets exactly one outcome on each scored dimension. rows are checked top to bottom, and the first match wins. `floor` = the chart's floor for that confidence, default 0.9. all charts so far expect voice for their whole scoreable time.

  | frame (analyzer spec §3.9) | pitch | resonance |
  |---|---|---|
  | `VoicingConfidence` < floor | unreliable | unreliable |
  | Silence | unattempted | unattempted |
  | Unvoiced or Creak | Miss (creak indicator for Creak, §1.2) | Miss |
  | Voiced, `F0Range` = Above | Miss | per resonance rows below |
  | Voiced, `F0Range` = Below, or `F0Confidence` < floor | unreliable | per resonance rows below |
  | Voiced, `F0Confidence` ≥ floor | judged on cents error | per resonance rows below |
  | ↳ resonance signal fails validity or its confidence < floor | — | unreliable |
  | ↳ resonance signal valid and confident | — | judged on its per-dimension error |

  the voicing-state rows mean a *confidently* detected Creak or Unvoiced frame is a Miss, as §1.2 requires. only an uncertain state abstains. pitch and resonance abstain independently: a frame can be pitch-judged and resonance-unreliable. a Voiced frame above the analyzer's ceiling is a Miss. the analyzer detects that case directly (analyzer spec §3.4), and charts must be authored ≥ 100 cents inside the search range, so the pitch is always a full Miss window off target. `Below` comes from a weaker fallback signal, so it abstains. resonance validity and confidence are whatever the Gate B signal ships with (analyzer spec §3.9, §1.5 here).
- **exclusions**: unreliable frames and grace-window frames are removed from that dimension's accuracy numerator *and* denominator. unreliable frames still count against completion. grace windows don't, since they were never scoreable time.
- **completion**, per dimension = judged frames (Misses included) / (chart frames − grace frames). unattempted and unreliable frames both lower it. a run where any graded dimension has completion < 60% is shown as "incomplete," ungraded, and cannot set PBs.
- **unreliable runs**, per dimension: >20% unreliable frames (of chart frames − grace frames) on a dimension means that dimension is "tracking unreliable."
  - pitch unreliable → no grade, no PB (§1.2), whatever resonance did.
  - resonance unreliable, pitch fine → graded on pitch only. it's labeled "pitch-only run — resonance tracking unreliable" and cannot set PBs on multi-dimension charts. this is the same treatment as vowel mismatch.

  exclusion is analyzer-driven and cannot be triggered per-frame by the user. paired with the completion rule, going quiet to protect a score just produces an incomplete run.
- **per-dimension score** = accuracy (mean frame value over judged frames) × completion, on a 0–1 scale.
- **multi-dimension**: score = Σ wᵢ·scoreᵢ, default weights pitch 0.6 / resonance 0.4, then the floor rule: `final = min(weighted mean, weakest dimension + 0.15)` on the 0–1 scale. you cannot S-rank with any dimension below ~0.80.
- **vowel mismatch** (§1.6): the resonance dimension is dropped, the run is graded on pitch only, labeled "pitch-only run," and cannot set PBs on multi-dimension charts.
- **PB provenance**: every PB stores `(chartVersion, scoringVersion, analyzerVersion)`. a change to any of the three archives existing PBs (still visible, labeled with their version) and starts fresh — rule changes never silently compare against old numbers, in either direction.

---

# part 5 — change log vs v1

**from the four direction decisions (2026-08):** hybrid chart targets — absolute scored library authored inside a near-universal safe band, personalized campaign/practice charts (resolves the v1 contradiction between "same charts for everyone" and range-mapped, range-gated highways); friends-only opt-in comparison replaces leaderboards at launch; blind rating exchange cut from the roadmap to a research note, §1.3 rewritten around an honest non-answer, and §1.11's model rescoped around the resulting data gap; resonance lane gets a two-layer design (formants as measured truth, validated brightness proxy as acceptable gameplay signal) so the MVP ships either way.

**v2.1 addendum (2026-08-19):** the motivation model made explicit as a solo journey — every primary reward loop (in-run juice, personal bests with end-of-run deltas, near-miss surfacing, session summaries, mastery ladder, milestones) closes without any other player; friends comparison stays encouraged but strictly secondary, with no reward locked behind it. PBs, deltas, near-miss prompts, and session summaries moved into the MVP (phase 1d) since they're what makes the "wants to replay" exit criterion work. all engagement mechanics explicitly subordinated to §1.9 safety: rewards attach to execution and showing up, never to intensity, and the session soft-stop silences "one more try" prompts.

**v2.2 (2026-08-19, second Sol review):** phase 0 exit split into Gate A (analyzer viability, after analyzer step 6, owned by the analyzer spec) and Gate B (product readiness, after step 8, owned here — resonance-signal decision, legibility, latency thresholds), resolving the cross-document gate mismatch; §1.5's "no extra work" claim replaced with a real resonance validation gate backed by a new resonance-manipulation corpus task and brightness-proxy contract in analyzer spec v2; the universal scored band explicitly labeled a hypothesis with a pre-defined eligible-denominator fallback; normative scoring appendix added (frame values, segment-vs-frame accounting, exclusion and completion math, weights and floor, vowel-mismatch policy, PB provenance); replay auto-recording reconciled with the privacy rules (in-memory/temp-encrypted ephemeral audio, crash sweep, pinning as the consent moment); lessons 6 and 8 made educational/ungraded in MVP instead of gating on phase 2 mechanics; assessment rescoped to write observed range and safety only, with goal bands user-chosen and editable from MVP; the safe-range assessment protocol made an SLP-authored phase 1a blocker; §1.11 rescoped — goal-band analytics (no perceptual claims) replace the diagnostic role, and the perception model is off the roadmap with a written reopening bar.

**from review:** judgment windows widened to ≥2× tracker error (v1's Perfect window equaled the phase 0 FPE gate, so tracker noise would grade users); miss segments with debounce, onset/breath grace, honest creak indication, and an unreliable-run abstention rule added to grading; the replay/listen-back loop promoted to a core MVP mechanic with pinning and A/B compare (it was absent from v1 despite being half the product's premise); "three-surface architecture" corrected to four; latency claims updated to analyzer v2's measured definitions (~50–110 ms user-to-photon) with scoring compensated by the calibrated offset; phase 0 gate text aligned with analyzer v2's slice-minimum/regression-gate semantics; weight's dependency on analyzer phase 1 (pulse-synchronous analysis, corrected H1–H2) made explicit, with an unscored meter allowed earlier; `AudioEffectCapture` demoted from assumption to evaluated option against native capture; resonance encoding made redundant (colour + non-colour) as a design constraint rather than an accessibility retrofit; rest days made streak-safe with a soft daily cap on high-intensity practice; voice profile bands changed to user-set goal bands with optional off-by-default reference overlays (also resolves the v1 non-binary open question); vowel-mismatch guard added as unscored feedback; Steam Deck mic added as a phase 0 corpus condition; cloud saves explicitly exclude audio.

**v2.3 (2026-09-28, paired with analyzer spec v2.3):**

- **resonance**: specificity added as a §1.5 validation criterion and a Gate B requirement, because a brightness proxy confounded with spectral tilt would reward pressed phonation (a §1.9 violation) and score lighter phonation as darker. added a risk row. added a formant-confidence caveat for high f0 on the measured-truth layer.
- **confidence and latency**: "low-confidence" defined against per-chart floors (default 0.9) on calibrated analyzer confidences (§1.2, appendix A). Gate A adds breathy-slice coverage. the Gate B capture-to-result threshold becomes < 10 ms under the analyzer's clarified definition, where the old < 25 ms sat on an ambiguity.
- **reference tones**: added as an open decision with options, plus a risk row.
- **capture path**: the comparison moves into phase 0 via the Godot probe (1a).
- **per-dimension abstention** (Astra review): the single "F0Confidence below floor" rule is replaced by a per-dimension outcome table (appendix A). `VoicingConfidence` decides state judgments, so a confident Creak or Unvoiced frame is a Miss instead of an exclusion. `F0Confidence` decides voiced pitch. resonance has its own validity and confidence rules. completion and the unreliable-run rule are per dimension. an unreliable resonance lane downgrades the run to pitch-only instead of voiding it. above-ceiling f0 is a Miss, and chart pitch targets must sit ≥ 100 cents inside the search range (1b).
- **housekeeping**: `SpectralTiltDbPerKhz` noted for the weight meter (§1.4). cross-references fixed: §1.2's safe-range gate → §1.9, §1.3's perception model → §1.11. spec references renamed to the unversioned `voice-analysis-spec.md`, since version history now lives in git.
