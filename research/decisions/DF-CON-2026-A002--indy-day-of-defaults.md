---
id: DF-CON-2026-A002
title: Indy Init day-of defaults for launch mode, slice policy, trace visibility, deployment, AI transport, persistence auth, organizer sign-off and demo scenario
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-10-08
updated: 2026-10-08
research_area: factory-bootstrap
decision_type: product
related_documents:
  - DF-CON-2026-A001
  - docs/indy-day-of.md
  - examples/indy-init.conditor.json
tags: [decision, indy-init, defaults]
confidence: medium
provenance:
  contributions:
    EXE-20261008T120253862Z-ae6f0581:
      operations: [created]
      at: 2026-10-08T12:02:54.191Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the Indy day-of defaults the user accepted and the RQR-001/003/004/008 defaults"
---

# Context

The day-of readiness review left eight items open (permission mode, slice
policy, trace visibility, RQR-005 deployment, RQR-003 AI, RQR-004
persistence authentication, RQR-001 organizer sign-off, RQR-008 demo
scenario). The user accepted the coordinator's recommendations for the first
four, and asked for the most defensible default for the rest, grounded in the
pinned Indy-init planning set (012f397).

These are competition-profile defaults. Product decisions remain
ReversibleChoice in the planning set (D-203, D-210, D-212, OQ-009, OQ-010,
OQ-042). The agent receives each one as acceptance criteria on the slice it
governs (`requirementsImport.acceptance` in the preset), because Indy-init is
read-only input and its docs are not changed.

# Decisions

1. **Launch permission mode: `auto`.** `conditor supervise` and every
   Claude launch keep `--permission-mode auto`. The tooling has no
   bypassPermissions fallback. R0 must prove that `auto`, with the
   scaffold's `.claude/settings.json`, lets the agent merge its own pull
   requests once their checks are green. Any change of mode is the user's
   call at R0.
2. **Slice policy.**
   - `demo-readiness` is never-cut, because a demo is required to compete.
   - `security-hardening` stays cut-last. Baseline security is part of the
     never-cut slices as acceptance criteria:
     - `bootstrap`: no credential in the repository, the client, Praxis
       state, reports or logs, with a CI check; a restrictive CSP on every
       page;
     - `workspace`: input validated at the domain boundary, and untrusted text
       rendered inert;
     - `ai-proposals`: the key lives in memory only.
   - `multi-agent` stays stretch. RQR-007 does not name it as a signature
     feature: it asks the question. The kickoff manifest, the only ordering
     source, says `stretch`.
3. **Trace.** The trace stays the `requirements-trace.json` attachment of
   `COND-MISSION-001` (DF-CON-2026-A001). `factory-proof` must publish a
   generated, read-only trace view of it in the Factory View (S20, S21) for
   the judges.
4. **Deployment (RQR-005): GitHub Pages.** The day-of command is
   `conditor repo create ... --deploy github-pages`. `demo-readiness` must
   add `npm run build` writing `dist/`.
   - Grounds: D-203 and P26.1, a static-capable client.
   - Needs: Pages on a private repository requires a paid GitHub plan. If
     the account lacks one, the Pages step fails (and is printed for manual
     follow-up), or the repository must be public. R2 must prove which
     applies.
5. **AI transport (RQR-003): direct browser-to-provider calls with a key the
   user enters at runtime, held in session memory only.** It sits behind a
   provider interface so a small proxy can replace it (D-212), with manual
   use and a labelled recorded proposal as the fallback (P16.7, P33.13,
   P38.38).
   - Grounds: a static Pages client cannot hold a secret (P26.1). A proxy
     needs hosting, an account and a deployed secret, none of which exist.
     A runtime key never stored satisfies P35.3 and SECURITY-THREAT-MODEL
     section 5. OQ-010 says a backend *may* be forced by secret handling;
     this choice avoids storing any secret.
   - Needs: **an AI provider API key and a spend cap set on the provider
     account. Neither exists yet; the user must create them before the
     event.** The provider must accept browser-origin calls; R4 must verify
     this.
6. **Persistence authentication (RQR-004): a fine-grained personal access
   token, used as a documented temporary deviation.** The token is scoped to
   one separate investigation data repository, with contents read and write
   only and a short expiry. It is entered at runtime and held in memory
   only. GitHub App with token exchange is recorded as the production
   target.
   - Grounds:
     - OAuth and device flow from a browser need a token-exchange backend,
       because the GitHub token endpoints do not allow browser CORS, and no
       such backend or account exists;
     - SECURITY-THREAT-MODEL sections 4 and 6 allow a documented temporary
       approach;
     - P35.2 and R11.1 say "should prefer", not "must";
     - local-first persistence stays never-cut, so the app works without the
       token;
     - a separate data repository keeps the token from touching application
       code (R11.2).
   - Needs: **the data repository and the token do not exist. The user
     creates both at or after kickoff; Conditor creates neither.**
7. **Organizer sign-off (RQR-001).** This needs organizer action, so nothing
   is decided on the organizers' behalf.
   - Recommended position: Conditor materializes at kickoff, with provenance
     recorded:
     - the published planning documents;
     - the 25 Percepta screen contracts;
     - the agent instruction files;
     - the generic, application-free scaffold.
     The scaffold contains no application behaviour. Every line of
     application code is written after 9:30, and the kickoff baseline
     records the planning commit and versions.
   - Question for the organizers (not sent):
     > "At 9:30 we will create an empty repository and run our own tooling
     > to add (1) our published planning and requirements documents, (2)
     > screen contracts and pre-written acceptance criteria and tests
     > written as specifications, (3) agent instruction files, and (4) a
     > generic build, test and CI scaffold from our public framework, with
     > no application features. All application code is written after
     > kickoff, and the application stores its data in GitHub. Is each of
     > these allowed, and is anything required to be created live instead?"
8. **Demo scenario (RQR-008): the DEMO-PLAN order-delivery case.**
   - The case: an order never arrived; the system and the carrier both say
     delivered; the shipping address may have changed after the order was
     placed.
   - The agent seeds it on the day as ordinary investigation data through
     normal commands, with at least one falsified hypothesis, one blocking
     unknown and one contradiction. The product must not special-case it.
   - Grounds: it is the only scenario in the planning set and it is
     domain-neutral. It exercises falsification, contradiction and unknowns,
     which is what OQ-042 asks for. Authoring it at kickoff avoids pre-built
     application data (R0, RQR-001).

# Consequences

- The import plan digest changes, because demo-readiness is never-cut and
  the descriptions carry the criteria. `docs/indy-day-of.md` records the new
  digest and the day-of commands.
- Every default above is a ReversibleChoice. Changing one changes the preset
  and therefore the digest.
