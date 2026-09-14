---
name: setup-pstack
description: Configure which agent personas pstack uses per role. Routes each pstack role to an opencode persona. Use for /setup-pstack, "configure pstack personas", or changing pstack's role routing.
---

# Setup pstack

Route pstack's roles to agent personas. Under opencode there are no model slugs or reasoning budgets — role variety comes from personas × skills, so this skill records which persona drives each role.

## Steps

### 1. List available personas

The dependable source is the harness agent roster for this session. The known opencode roster is: aegis, athena, brunel, carter, curie, daedalus, fisk, holmes, hopper, lynx, magellan, mercury, parker, tyrell, vitruvius, wattson. If the harness exposes more personas than these, prefer the live roster for completeness. Never route a role to a persona you have not confirmed exists.

### 2. Load current state

The default role-to-persona mapping is the routing shape shown in step 5 below. If a previous routing was already confirmed in this repo's docs or conversation, treat its role values as the current choices. Otherwise start from those defaults.

### 3. Map and confirm

**(a) Build the working table.** Build the working table from the defaults below, and on a re-run keep any role routing the user previously changed.

**(b) Show the roles and confirm.** Show every role with its persona. Ask whether to accept as-is or change specific roles, offering the roster personas as the options. For panel roles (arena runners, architect runners, interrogate reviewers) the value is a list, and one subagent runs per entry, so the list length sets the count. `arena cross-judge pool` is also a list, but Arena selects one value from it whose persona differs from the parent's when possible. `swarm workers` is the default persona for every worker unless a race or comparison assigns another persona per arm.

Default routing (persona temperament, not model capability):

- Deliberate/judgment roles → holmes, athena, curie, fisk
- Build/execution roles → brunel, daedalus, hopper, wattson, mercury
- Triage/scoping roles → magellan, lynx, carter, parker, aegis, tyrell, vitruvius

### 4. Validate

Every persona routed must be in the confirmed roster. If a chosen persona does not exist, stop and ask again.

### 5. Record the routing

Confirm the routing in-session: it applies to subagents spawned from here on. There is no model-slug rule file to write under opencode — do not write model slugs, budget labels, or effort tokens anywhere. Overwrite any earlier in-session routing wholesale so re-runs stay idempotent. Shape (one line per role, using the same labels poteto-mode uses):

```
# pstack persona routing. One line per role.
feature, refactoring: brunel
bug-fix: daedalus
perf-issue: wattson
hillclimb: hopper
judgment and prose: holmes
hardest tasks: athena
how explorer: magellan
how explainer: curie
why investigators: lynx, carter
why synthesizer: fisk
reflect tooling: mercury
reflect judgment, divergent, synthesizer: athena
arena runners: holmes, curie, fisk, athena
arena cross-judge pool: holmes, curie, fisk, athena
swarm workers: brunel
architect runners: vitruvius, parker, aegis, tyrell
interrogate reviewers: holmes, athena, fisk
```

Note (alternate harness only, not used under opencode): upstream pstack on Cursor wrote model slugs plus a budget label to `~/.cursor/rules/pstack-models.mdc`. That path is out of scope here.

### 6. Confirm

Tell the user the routing was recorded and that it applies to new subagents spawned in this session. Re-running this skill updates it.

### 7. Offer a verification skill (optional)

Check whether the project has a way to drive the real app for proof (a `verify-*` skill, or an existing harness). If not, offer once: "want a project-local verification skill, so agents can drive the app the way a user does and prove changes work? I can generate one with /create-verification-skill." On yes, invoke `/create-verification-skill` (resolves wherever pstack is installed: workspace, user, or plugin). On no, move on without pushing.
