# Retrieval Evaluation Harness

Tests whether the domain file retrieval pipeline finds the correct document sections for given queries.

## Structure

```
retrieval-eval/
├── documents/           # Test documents (place PDF and DOCX files here)
├── eval_cases.yaml      # Question & expected-answer pairs
├── eval_runner.py       # Evaluation script
├── requirements.txt     # Python dependencies
└── README.md
```

## Setup

```bash
cd ava-kb/tests/retrieval-eval
pip install -r requirements.txt
```

## Adding Test Cases

Edit `eval_cases.yaml`. Each case has:
- **question** — the user query
- **source_document** — which file should be retrieved from
- **expected_sections** — the document sections (heading + key content) the pipeline should surface
- **min_similarity** — cosine similarity threshold (default: 0.70)
- **depends_on** — (optional) case ID that must run first (for follow-up questions with prior context)

## Running

```bash
python eval_runner.py                          # Offline self-test (validates framework)
python eval_runner.py --mode api --domain-id X # Test full AVA API pipeline
python eval_runner.py --mode direct --kb-id X  # Test ava-kb search directly
```

Options:
- `--mode` — `offline` (default), `api`, or `direct`
- `--api-url` — AVA API base URL (default: http://localhost:5000)
- `--kb-url` — ava-kb base URL (default: http://localhost:8100)
- `--domain-id` — Domain ID for api mode
- `--kb-id` — KB ID for direct mode
- `--threshold` — global similarity threshold override
- `--backend` — `auto` (default), `st` (sentence-transformers), or `tfidf` (scikit-learn fallback)
- `--model` — sentence-transformers model name (default: all-MiniLM-L6-v2)
- `--verbose` — show per-chunk similarity scores
- `--case` — run a single case by ID

## Baseline Snapshots

The `baseline-before.yaml` and `baseline-after.yaml` files record before/after results:

```bash
# Run baseline (save output manually to the YAML):
python eval_runner.py --mode api --api-url http://localhost:5010 --domain-id ctms --backend tfidf --verbose
```

| Metric | Before | After |
|--------|--------|-------|
| Pass rate | 2/6 (33%) | TBD |
| Avg similarity | 0.479 | TBD |

## Similarity Backends

- **sentence-transformers** (preferred): Uses `all-MiniLM-L6-v2` for semantic similarity. Requires HuggingFace model download.
- **tfidf** (fallback): Uses scikit-learn TF-IDF + cosine similarity. No downloads needed, works in corporate environments. Less accurate for semantic matching but good for keyword-heavy content.

The script auto-detects which is available (use `--backend` to force one).
