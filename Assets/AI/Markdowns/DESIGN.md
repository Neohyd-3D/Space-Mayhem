# DESIGN.md

# PROJECT

Arcade Racing HUD

Visual Direction:
Inkshadow (League of Legends) × Sunset Overdrive × Arcade Racing × Futuristic Street Culture

Purpose:
Create a racing HUD that is instantly readable at high speed while expressing aggression, momentum, personality, and street-racing energy.

---

# CORE DESIGN PRINCIPLES

## SILHOUETTE FIRST

The silhouette must be recognizable before any information is readable.

Avoid:
- rectangles
- symmetrical panels
- generic sci-fi frames

Prefer:
- aggressive diagonals
- blade-like cuts
- stretched trapezoids
- asymmetrical weight distribution

Test:
View at 20% scale.
If recognizable immediately → PASS.

---

## 70 / 20 / 10 RULE

70% readability

20% mechanical structure

10% visual chaos

The HUD is a gameplay tool first.

---

# INKSHADOW INFLUENCE

Use:
- brush strokes
- ink splatters
- rough painted edges
- broken line work
- calligraphic energy

Avoid:
- perfect vectors
- sterile geometry
- overly clean outlines

Rule:

The frame should feel painted.

Not manufactured.

---

# SUNSET OVERDRIVE INFLUENCE

Use:
- graffiti aesthetics
- sticker-like graphic shapes
- exaggerated silhouettes
- rebellious visual energy
- paint explosions
- high contrast color blocks

Avoid:
- military seriousness
- corporate UI language
- minimalism

Rule:

The UI should feel like a racer customized it.

---

# SHAPE LANGUAGE

Primary:
- slashes
- blades
- stretched hexagons
- angular cuts

Secondary:
- paint streaks
- drips
- brush smears
- torn corners

Forbidden:
- circles as dominant elements
- soft rounded corners
- floating panels

---

# COLOR SYSTEM

Primary Accent:

Orange

Examples:
- #FF6A00
- #FF7A00
- #FF9A00

Secondary:
- White

Base:
- #080808
- #101010

Accent Distribution:

80% dark base

15% orange

5% white

---

# SURFACE TREATMENT

Every frame should contain:

- halftone textures
- paint wear
- overspray
- brush scratches
- splatter accents

Important:

Only edges receive damage.

Data zones remain clean.

Rule:

Dirty frame.
Clean information.

---

# TYPOGRAPHY

References:
- Sunset Overdrive
- Jet Set Radio
- Inkshadow Event Graphics

Properties:
- uppercase
- bold
- slightly irregular
- hand-painted energy

Labels:
LAST
BEST
LAP

should feel integrated into the art.

Not added later.

---

# RACING READABILITY RULES

Player must read:

1. Current Lap
2. Current Time
3. Best Time
4. Last Time

Within:

< 300ms glance

Rules:

- never place critical data over texture
- maintain strong contrast
- avoid decorative elements near numbers
- use monospaced timing values

Timing text should always be the cleanest element.

---

# BEST / LAST MODULE

Location:

Bottom strip

Integrated into frame

Not floating

Structure:

[ LAST ] [ BEST ]

Requirements:

- embedded labels
- painted appearance
- digital timing values
- separate background container

Visual Contrast:

Graffiti labels

Precise numbers

---

# MOTION LANGUAGE

Animations should feel explosive.

Use:
- overshoot
- impact frames
- paint wipes
- smears
- slash reveals

Avoid:
- linear fades
- sterile scaling

Panel Entry:

Paint slash
→ frame assembles
→ glow activates

Panel Exit:

Frame tears apart
→ paint exits screen

---

# UNITY IMPLEMENTATION

Separate Assets:

1. Frame
2. Graffiti Layer
3. Labels
4. Data Layer
5. Glow Layer
6. FX Layer

All orange accents must be recolorable.

Use masking whenever possible.

---

# MODULARITY RULES

Panels should support:

- color swaps
- seasonal skins
- faction skins
- accessibility themes

Never bake text into the frame texture.

Only decorative labels.

All gameplay text remains dynamic.

---

# 9-SLICE RULES

Stretchable Areas:
- center data zones

Fixed Areas:
- corners
- paint splatters
- silhouette cuts

The silhouette must never distort.

---

# DESIGN TEST

Remove all text.

Can you still identify the HUD?

PASS.

Remove orange.

Does it become generic?

FAIL.

Does it feel like a street racer vandalized a military HUD?

PASS.

Does it feel fast even when static?

PASS.
