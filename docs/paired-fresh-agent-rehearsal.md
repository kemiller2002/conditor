# Paired Fresh-Agent Rehearsal

This is the next validation layer after Conditor's deterministic empty-repository CI rehearsal.

It runs two independent implementation lanes against the same sacrificial application:

- OpenAI/Codex
- Claude

Each lane receives its own empty repository. The two agents do not see each other's output.

## Run

From a Conditor checkout with both launchers authenticated:

```bash
bash scripts/rehearse-agent-pair.sh
```

Optionally choose the parent output directory:

```bash
bash scripts/rehearse-agent-pair.sh /tmp/echelon-factory-pair
```

The harness invokes `rehearse-fresh-agent.sh` independently for each lane, so both lanes must:

1. consume the same pinned rehearsal preset;
2. discover the same canonical kickoff contract;
3. complete the same live Praxis mission;
4. produce the same seven categories of completion evidence;
5. pass the same independent scorer.

## Outputs

The parent directory retains:

```text
openai/                 complete OpenAI target repository
claude/                 complete Claude target repository
openai.log              provider/factory transcript
claude.log              provider/factory transcript
openai.exit-code
claude.exit-code
comparison.json
```

Nothing is deleted on failure.

## Comparison discipline

The pair run is not a model popularity contest.

Compare observable outcomes:

- whether the lane passed;
- clarification/intervention count;
- architecture violations;
- contract-discovery failures;
- test/build evidence;
- churn/rework;
- Aegis/ROS/Praxis evidence;
- elapsed time;
- model/tool cost where available;
- independent score failures.

Do not declare a general model winner from one rehearsal.

The useful question is:

> What did each lane reveal that the factory failed to make deterministic?

A failure that would affect any capable agent belongs back in Conditor, Ordo, ROS, Percepta, Forma, Limen, Aegis, Praxis, Folio, or the canonical contract.
