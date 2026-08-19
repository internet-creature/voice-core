# voice training game — design doc & roadmap

companion to `voice-analysis-spec.md`, which covers phase 0. this doc picks up after the analysis prototype clears its decision gate.

---

# part 1 — design decisions

these are settled. everything in the roadmap assumes them.

## 1.1 the core principle

**score the performance, never the person.**

every number the game shows is about *how well you executed this exercise, tonight*. no number is about how feminine, masculine, or passing your voice is. this isn't a hedge — it's what makes the retry loop work. score-chasing depends on failure being cheap, and a bad run has to mean "bad run," not "you don't pass."

the corollary: the *targets* come from the curriculum and from goals the user sets with their coach. the game scores how close they got. it never decides what they should be aiming for.

## 1.2 the three-surface architecture

| surface | what it measures | timescale | comparable? |
|---|---|---|---|
| **chart grade** | execution accuracy on one run | seconds | yes — same charts for everyone |
| **collection score** | mastery across the chart library | days–weeks | yes |
| **progress trends** | the voice itself, over time | months | no — personal only |
| **voice profile** | where each attribute sits now | current state | no — multi-axis by design |

### chart grade — the engagement loop

per-exercise, per-run. **S / A / B / C / D**, plus a numeric score. resets every attempt.

grading:

```
per-frame error from target (cents, or the relevant unit per dimension)
  Perfect  < 15 cents
  Great    < 35
  Good     < 70
  Miss     ≥ 70  (or unvoiced when the chart expects voice)

chart score = weighted mean of frame accuracy × completion, scaled to 1,000,000

letter thresholds:  D 50%  |  C 65%  |  B 78%  |  A 88%  |  S 95%
Full Combo = zero Miss frames
```

for multi-dimension charts (pitch + resonance), score each dimension separately then combine as a **weighted mean with a floor** — you must not be able to S-rank by nailing pitch and ignoring resonance.

**tier names stay semantically empty.** S, SS, SSS, AAA. never "Passing," never "Feminine," never "Natural." the moment a tier has semantic content about gender, the whole problem comes back through the copy.

### collection score — the meta number

`"S-ranked 34 of 60 exercises"` / `"full-comboed the Resonance campaign"` / a mastery percentage across the library.

this is the number that goes on leaderboards and gets compared with friends. it works because:

- huge range, effectively no ties
- fair — everyone plays the same charts
- moves on a timescale of days, not years
- someone with the hardest starting voice can still 100% it
- cannot be read as a verdict on whether they pass

### progress trends — retention

separate screen. personal, never compared, **no letter grades and no failure pole.**

median speaking f0 over 30 days, resonance estimate trend, weight, intonation range. sparklines. `"up 18 Hz since March."`

this is where most skill apps fail — they can't show slow progress on a slow skill, so users conclude nothing is happening and leave. precise measurement is your advantage here; use it.

### voice profile — the honest "where am I"

multi-axis display: pitch, resonance, weight, intonation, each against typical range bands. radar chart, stat page, whatever fits the art direction.

deliberately **not collapsed to a scalar**, which means it structurally cannot be leaderboarded. that's the feature.

## 1.3 the passing question

users will want to know whether they pass. don't fake an answer with a model, and don't put it in the progression system.

**blind community rating exchange.** opt-in. record a clip, it goes out anonymized, other users hear audio only with no context and rate it. you get back a distribution: `"8 of 12 listeners heard this as female."`

that isn't an approximation of perceived gender — it *is* perceived gender, measured the way the research literature measures it. and it quietly generates the rated corpus you'd need for a model later.

needs real abuse design before shipping: rate limits, reporting, a reputation gate on raters, and a clear policy. scope it properly; don't bolt it on.

## 1.4 what gets measured

| tier | attributes | in scope |
|---|---|---|
| 1 | f0 (median, stability, glide accuracy) and **resonance via formants** | phase 1 |
| 2 | vocal weight (spectral tilt, H1–H2, CPP), intonation range and contour | phase 2 |
| 3 | breathiness, /s/ centroid, rate, articulation precision | not gamified |

**resonance is not optional in the MVP.** it's roughly co-equal with pitch in the literature on perceived gender, and shipping a "voice training game" that only tracks pitch would be a legitimate criticism.

**breathiness stays out of scored mechanics.** it's widely taught as a feminization lever but the evidence that it moves gender perception is genuinely mixed. teach it if the SLP says to; don't build a rank around it.

**volume is not weight.** RMS measures loudness. weight is fold mass, and it shows up in spectral tilt and CPP. if the tutorial teaches weight and the game scores loudness, learners optimize for the wrong thing and can't articulate why they're stuck.

## 1.5 the fixed-vowel rule

**every exercise specifies its vowel.** this is free — the game tells the user what to say anyway — and it's what makes F1/F2 comparable across sessions and across users. without it, a formant meter is mostly a vowel detector.

tag every logged frame with the expected vowel so batch analysis can group correctly.

## 1.6 tracing, not hitting

the analysis chain has a hard latency floor of roughly 40 ms — YIN needs 2–3 periods, and at 80 Hz that's 25–37 ms of audio before any answer exists. it cannot be engineered away.

so: **sustained lines, glides, and contours.** no tight timing windows, no discrete note-hits with millisecond judgment. the highway is something you trace, not something you strike.

## 1.7 the highway

- **log-frequency vertical axis**, mapped to the user's assessed range rather than absolute Hz
- **Hz labels visible** — the community talks in Hz and will want the numbers
- second tracked dimension (resonance) encoded as line colour/thickness/glow rather than a second position axis
- neon glow via Godot's HDR 2D bloom — push emission above 1.0 rather than faking it in shader
- display smoothing (causal median + slew limit) is separate from scoring, which uses raw frames

## 1.8 safety

vocal strain is a real injury risk and this is the part that must not be an afterthought.

- **range gating.** the baseline assessment sets a safe ceiling and floor. the game never generates a chart that pushes past it. the ceiling expands only as the assessment moves.
- **warmups** required before higher-intensity exercises
- **session length limits** with a soft stop, and a visible "stop if it hurts" convention that appears in the exercise UI, not just in a disclaimer nobody reads
- progression keys off **practice consistency and chart execution**, never off pushing higher or louder

## 1.9 privacy

- **all analysis is local.** no audio leaves the machine by default.
- the blind rating exchange is explicit opt-in, per-clip, with a visible confirmation of what's being shared
- say all of this plainly on the store page — for this audience it's a purchase decision, not fine print
- voice is treated as biometric data under Illinois BIPA, Texas CUBI, and Washington state law. get counsel before any feature that transmits or stores audio server-side.

## 1.10 the model, eventually

- **v1: interpretable.** logistic regression or a GAM over f0 stats, VTL estimate, spectral tilt, and intonation variability, fit on a few hundred perceptually-rated clips. runs locally in microseconds. crucially it can say *"pitch is in range, resonance is your limiting factor"* — a black box can't, and a teaching tool that can't explain itself isn't teaching.
- **v2: learned, on your own consented corpus.** frozen wav2vec2 or WavLM as a feature extractor with a small regression head. train on ratings from the community exchange, not identity labels scraped from video platforms.
- **legal bulk audio: Mozilla Common Voice** (CC0, ~30k hours, self-reported gender metadata, read speech — closer to your use case than social media audio). useful for pretraining and calibration; still not a substitute for perceptual ratings.
- output a **distribution with an interval**, never a point estimate.

## 1.11 commercial

- **one-time purchase.** the incumbents are subscription mobile apps; this is a real differentiator and it matters for an audience that skews financially precarious.
- generous regional pricing
- consider a gifting or need-based path
- Steam Deck as an explicit target — heavy overlap with the audience, and the Deck has a usable built-in mic

## 1.12 community

a trans-focused title on Steam will attract exactly what you'd expect. decide before launch, not during:

- Steam discussion board disabled or heavily moderated, with a moderated Discord as the real venue
- a written review-bomb response plan
- moderation policy for the rating exchange written before the feature ships

---

# part 2 — roadmap

## phase 0 — analysis prototype ✅ spec'd separately

decision gate: GPE < 2%, VDE < 5%, FPE < 15 cents on the **worst** corpus slice, and measured p95 latency under 50 ms.

if the noisy and creaky slices won't come down, change approach here. it's cheap now and expensive everywhere after.

## phase 1 — MVP

**goal:** one person can install this, be taught the fundamentals, and practice one exercise repeatedly with trustworthy feedback.

**engine:** Godot 4.x with C#. VoiceCore is referenced as a .NET project — unchanged from phase 0, no rewrite.

### 1a — integration and calibration

- VoiceCore wired into Godot, mic capture via `AudioEffectCapture`
- **onboarding flow**: device selection, input gain, noise floor measurement, latency offset (loopback or manual tap-to-sync)
- **baseline voice assessment**: comfortable speaking f0, range floor and ceiling, resonance baseline, one read passage
- assessment output writes the safe-range gate

### 1b — the highway

- log-frequency pitch line with HDR neon glow
- resonance as a second visual dimension
- one background theme
- chart format defined and serialized (a chart is: target contour, vowel, duration, tolerance bands, dimensions scored)

### 1c — tutorial campaign

6–8 lessons. **an SLP or gender-affirming voice coach writes or reviews the actual content** — this skeleton is a placeholder for structure, not curriculum:

1. how your voice works — anatomy, folds, vocal tract. orientation and safety, no exercise.
2. safety and warmup — the rules, warning signs, when to stop
3. finding your baseline — reading your own assessment
4. pitch — what it is, sirens and glides
5. resonance — the big one. tract size, brightness, why pitch alone isn't enough
6. weight — thick vs light, and why it isn't volume
7. putting it together — sustained targets across dimensions
8. into speech — carrying it into a phrase

each lesson: explanation → guided demo → gated practice chart.

### 1d — one practice mode + grading

**Hold** — sustain a target line at a set pitch and resonance. scores stability and duration. simplest mode, hardest to get right, and it's the one every other mode is built on.

chart grading per §1.2. save/progress. local profile.

### 1e — settings and accessibility

- colourblind-safe palettes (you're relying heavily on colour for resonance)
- glow/flash intensity slider, and a reduced-motion mode — this matters both for photosensitivity and because neon-heavy visuals are fatiguing over long practice sessions
- font scaling, full remapping
- an audio-only feedback option

**phase 1 exit:** a stranger can install it, complete onboarding without help, finish the campaign, and want to replay the Hold charts.

## phase 2 — alpha

**goal:** it's a game, not a demo.

- **three more practice modes** reusing the highway:
  - **Glide** — follow a moving contour. scores tracking error.
  - **Steps** — hit discrete targets and settle. scores settle time and overshoot.
  - **Phrase** — read a prompt against a target contour overlay. scores contour match and median f0. this is the bridge from exercise to speech.
- **weight and intonation** added as tracked and scored dimensions
- **collection score, ranks, achievements, skins** — cosmetics keyed to practice consistency and chart clears
- **progress trends** and **voice profile** screens
- **Steam integration**: achievements, cloud saves, rich presence
- **store page live** — wishlists compound, get this up at the start of the phase, not the end
- **closed playtest** with community voice coaches and 20–50 users. this is where the curriculum gets its real critique.

any bucketed metric introduced here needs **hysteresis** — harder to fall than to climb — and a trailing median over many sessions as its input. a composite of five noisy measurements has more variance than any one of them, and tier flicker from a head cold or a headset change is brutal.

## phase 3 — beta / early access

- **blind rating exchange** with full moderation tooling
- **interpretable perception model** (§1.10 v1)
- content expansion — more charts, more themes, campaign chapter 2
- Steam Deck verification
- Next Fest demo
- localization scoping

## phase 4 — launch and beyond

- learned model on the accumulated consented corpus
- community chart creation?
- FTM/masculinization curriculum as a distinct track — genuinely different pedagogy, not a mirror of the existing one

---

# part 3 — risks

| risk | severity | mitigation |
|---|---|---|
| tracker accuracy insufficient on real hardware | fatal | phase 0 gate; per-slice CI metrics |
| curriculum lacks credibility with the audience | fatal | SLP review budgeted as a line item, credited on the store page |
| scoring reads as a verdict on passing | severe | §1.1–1.2 architecture; audit every string in the UI for gender semantics |
| someone injures their voice | severe | range gating, warmups, session limits |
| review-bombing / harassment | high | §1.12 decided pre-launch |
| resonance UX illegible as a second dimension | medium | prototype it in phase 0 step 6, before committing chart design |
| scope creep into the ML model | medium | v1 interpretable only; learned model gated behind a real corpus |

---

# part 4 — open questions

- does the campaign lead with **pitch** or **resonance**? pedagogically defensible both ways, and it changes lesson order and which charts ship first. this is an SLP question.
- is resonance-as-colour legible, or does it need its own lane? answer empirically in phase 0.
- how do you handle a user whose goal isn't binary — androgynous or non-binary targets? the target-setting UI needs to support this from the start, not as a later patch, and the range-band visuals shouldn't imply two poles.
- **who reviews the curriculum, and when do you book them?** long lead time. start now.
