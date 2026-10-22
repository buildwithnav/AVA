"""
Retrieval Evaluation Runner
============================
Standalone script that evaluates the AVA domain file retrieval pipeline
against expected document sections using semantic similarity.

Usage:
    python eval_runner.py
    python eval_runner.py --verbose
    python eval_runner.py --case sdv-crfs-rdc-manual-question
    python eval_runner.py --mode direct  (test chunking/search directly against ava-kb)
    python eval_runner.py --mode api     (test full pipeline via ava-api)
"""

# SSL workaround for corporate environments (must be before other imports)
import os
import ssl

os.environ["CURL_CA_BUNDLE"] = ""
os.environ["REQUESTS_CA_BUNDLE"] = ""
os.environ["HF_HUB_DISABLE_SSL_VERIFY"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "0"

# Monkey-patch SSL for httpx/urllib3 in corporate environments
_original_create_default_context = ssl.create_default_context
def _patched_create_default_context(*args, **kwargs):
    ctx = _original_create_default_context(*args, **kwargs)
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    return ctx
ssl.create_default_context = _patched_create_default_context

import argparse
import sys
import time
from pathlib import Path
from typing import Optional, Protocol

import numpy as np
import requests
import yaml
from rich.console import Console
from rich.table import Table
from rich.panel import Panel
from rich.text import Text

# Try to import sentence-transformers; fall back to TF-IDF
_USE_SENTENCE_TRANSFORMERS = False
try:
    from sentence_transformers import SentenceTransformer, util as st_util
    _USE_SENTENCE_TRANSFORMERS = True
except ImportError:
    pass

from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.metrics.pairwise import cosine_similarity as sklearn_cosine


console = Console()

# Paths
SCRIPT_DIR = Path(__file__).parent
EVAL_CASES_PATH = SCRIPT_DIR / "eval_cases.yaml"
DOCUMENTS_DIR = SCRIPT_DIR / "documents"

# Defaults
DEFAULT_THRESHOLD = 0.70
DEFAULT_MODEL = "all-MiniLM-L6-v2"
DEFAULT_AVA_API_URL = "http://localhost:5010"
DEFAULT_AVA_KB_URL = "http://localhost:8100"


class SimilarityEngine:
    """Abstract similarity computation — supports sentence-transformers or TF-IDF fallback."""

    def __init__(self, backend: str = "auto", model_name: str = DEFAULT_MODEL):
        self.backend = backend
        if backend == "auto":
            self.backend = "sentence-transformers" if _USE_SENTENCE_TRANSFORMERS else "tfidf"

        if self.backend == "sentence-transformers":
            self._init_sentence_transformers(model_name)
        else:
            self._init_tfidf()

    def _init_sentence_transformers(self, model_name: str):
        console.print(f"[dim]Loading sentence-transformers model: {model_name}...[/dim]")
        start = time.time()
        try:
            self.model = SentenceTransformer(model_name)
            elapsed = time.time() - start
            console.print(f"[dim]Model loaded in {elapsed:.1f}s[/dim]")
        except Exception as e:
            console.print(f"[yellow]Failed to load sentence-transformers ({e}), falling back to TF-IDF[/yellow]")
            self.backend = "tfidf"
            self._init_tfidf()

    def _init_tfidf(self):
        console.print("[dim]Using TF-IDF similarity (scikit-learn). For better accuracy, install sentence-transformers and ensure HuggingFace access.[/dim]")
        self.vectorizer = TfidfVectorizer(
            stop_words="english",
            ngram_range=(1, 2),
            max_features=10000,
            sublinear_tf=True,
        )

    def compute_best_chunk_similarity(
        self, retrieved_chunks: list[str], expected_content: str
    ) -> tuple[float, int]:
        """Find the best-matching chunk and return (score, index)."""
        if not retrieved_chunks:
            return 0.0, -1

        if self.backend == "sentence-transformers":
            return self._st_best_similarity(retrieved_chunks, expected_content)
        else:
            return self._tfidf_best_similarity(retrieved_chunks, expected_content)

    def _st_best_similarity(
        self, chunks: list[str], expected: str
    ) -> tuple[float, int]:
        expected_emb = self.model.encode([expected], convert_to_tensor=True)
        chunk_embs = self.model.encode(chunks, convert_to_tensor=True)
        similarities = st_util.cos_sim(expected_emb, chunk_embs)[0]
        best_idx = int(similarities.argmax())
        return float(similarities[best_idx]), best_idx

    def _tfidf_best_similarity(
        self, chunks: list[str], expected: str
    ) -> tuple[float, int]:
        all_texts = [expected] + chunks
        tfidf_matrix = self.vectorizer.fit_transform(all_texts)
        # Compare expected (index 0) against all chunks (index 1+)
        similarities = sklearn_cosine(tfidf_matrix[0:1], tfidf_matrix[1:])[0]
        best_idx = int(np.argmax(similarities))
        return float(similarities[best_idx]), best_idx

    def keyword_presence_score(self, response: str, expected: str) -> float:
        """
        Extract distinctive keywords/phrases from expected content and check
        what fraction appear in the response. This handles paraphrasing better
        than TF-IDF since it only requires key entities to be present.
        """
        import re
        response_lower = response.lower()
        # Extract distinctive terms: capitalized words, technical terms, numbers with dots
        # Also extract multi-word phrases in quotes or distinctive patterns
        distinctive = set()
        # Technical identifiers (PKG_, MON_, SMART_, etc.)
        distinctive.update(re.findall(r'[A-Z][A-Z_]{3,}[A-Z0-9_]*', expected))
        # Numbered items like "4.4.1", "15338.1"
        distinctive.update(re.findall(r'\d+\.\d+(?:\.\d+)?', expected))
        # Quoted phrases or terms in asterisks
        distinctive.update(re.findall(r'\*\*([^*]+)\*\*', expected))
        # Key noun phrases (2-3 word capitalized sequences)
        distinctive.update(re.findall(r'[A-Z][a-z]+(?: [A-Z][a-z]+){1,2}', expected))
        # Important single technical words (>6 chars, not common)
        common_words = {'should', 'within', 'during', 'following', 'through', 'section',
                       'stores', 'system', 'information', 'monitoring', 'display'}
        for word in re.findall(r'\b[A-Za-z]{7,}\b', expected):
            if word.lower() not in common_words:
                distinctive.add(word)

        if not distinctive:
            return 0.0

        # Check what fraction of distinctive terms appear in response
        found = sum(1 for term in distinctive if term.lower() in response_lower)
        return found / len(distinctive)


def load_cases(path: Path, case_filter: Optional[str] = None) -> list[dict]:
    """Load eval cases from YAML file."""
    with open(path, "r", encoding="utf-8") as f:
        data = yaml.safe_load(f)

    cases = data.get("cases", [])
    if case_filter:
        cases = [c for c in cases if c["id"] == case_filter]
        if not cases:
            console.print(f"[red]No case found with id '{case_filter}'[/red]")
            sys.exit(1)

    return cases


def retrieve_via_api(
    api_url: str, domain_id: str, query: str, top_k: int = 15
) -> list[dict]:
    """
    Call the AVA API to retrieve chunks for a query.
    Returns list of {content, source, chunk_id, score}.
    """
    try:
        resp = requests.post(
            f"{api_url}/api/domain-files/search",
            json={"domainId": domain_id, "query": query, "topK": top_k},
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json().get("chunks", [])
    except requests.exceptions.ConnectionError:
        return []
    except requests.exceptions.HTTPError as e:
        console.print(f"[red]API error: {e}[/red]")
        return []


def retrieve_via_kb_direct(
    kb_url: str, kb_id: str, query: str, top_k: int = 15
) -> list[dict]:
    """
    Call ava-kb directly to search chunks.
    Returns list of {content, source, chunk_id, score}.
    """
    try:
        resp = requests.post(
            f"{kb_url}/kb/search",
            json={"knowledgeBaseId": kb_id, "query": query, "topK": top_k},
            headers={"X-KB-Api-Key": "change-me-in-production"},
            timeout=30,
        )
        resp.raise_for_status()
        results = resp.json()
        # Normalize response format
        chunks = []
        for r in results.get("chunks", results.get("results", [])):
            chunks.append({
                "content": r.get("content", r.get("text", "")),
                "source": r.get("source", r.get("metadata", {}).get("source", "")),
                "chunk_id": r.get("id", r.get("chunk_id", "")),
                "score": r.get("score", 0.0),
            })
        return chunks
    except requests.exceptions.ConnectionError:
        return []
    except requests.exceptions.HTTPError as e:
        console.print(f"[red]KB error: {e}[/red]")
        return []


def evaluate_case(
    case: dict,
    engine: SimilarityEngine,
    retrieved_chunks: list[dict],
    verbose: bool = False,
) -> dict:
    """
    Evaluate a single test case against retrieved chunks.
    Returns evaluation result with scores and pass/fail.
    """
    result = {
        "id": case["id"],
        "question": case["question"],
        "source_document": case["source_document"],
        "threshold": case.get("min_similarity", DEFAULT_THRESHOLD),
        "num_chunks_retrieved": len(retrieved_chunks),
        "section_scores": [],
        "best_score": 0.0,
        "passed": False,
        "source_match": False,
    }

    if not retrieved_chunks:
        result["error"] = "No chunks retrieved"
        return result

    # Check if correct source document was retrieved
    chunk_sources = [c.get("source", "") for c in retrieved_chunks]
    expected_source = case["source_document"]
    result["source_match"] = any(
        expected_source.lower() in s.lower() for s in chunk_sources
    )

    # Extract text content from chunks
    chunk_texts = [c.get("content", "") for c in retrieved_chunks]

    # Evaluate each expected section
    for section in case.get("expected_sections", []):
        key_content = section.get("key_content", "")
        if not key_content.strip():
            continue

        best_score, best_idx = engine.compute_best_chunk_similarity(
            chunk_texts, key_content
        )

        section_result = {
            "heading": section.get("section_heading", ""),
            "best_score": best_score,
            "best_chunk_idx": best_idx,
            "best_chunk_preview": (
                chunk_texts[best_idx][:200] if best_idx >= 0 else ""
            ),
        }
        result["section_scores"].append(section_result)

        if verbose and best_idx >= 0:
            console.print(f"    [dim]Best chunk ({best_score:.3f}):[/dim]")
            console.print(f"    [dim]{chunk_texts[best_idx][:150]}...[/dim]")

    # Overall score is the minimum across all expected sections
    # (all sections must be retrieved for the case to pass)
    if result["section_scores"]:
        scores = [s["best_score"] for s in result["section_scores"]]
        result["best_score"] = min(scores)
        result["avg_score"] = sum(scores) / len(scores)
        result["passed"] = result["best_score"] >= result["threshold"]
    else:
        result["error"] = "No expected sections to evaluate"

    return result


def run_offline_eval(
    cases: list[dict], engine: SimilarityEngine, verbose: bool = False
) -> list[dict]:
    """
    Run evaluation in offline mode — compare expected content against itself
    as a sanity check (should get ~1.0 scores), useful when no API is running.
    """
    console.print(
        Panel(
            "[yellow]Running in OFFLINE mode (no API/KB connection).\n"
            "This validates the eval framework itself by self-scoring expected content.\n"
            "Scores should be ~1.0. Use --mode api or --mode direct for real evaluation.[/yellow]",
            title="Offline Mode",
        )
    )

    results = []
    for case in cases:
        # Simulate retrieval by using expected content as "retrieved chunks"
        fake_chunks = [
            {"content": s["key_content"], "source": case["source_document"]}
            for s in case.get("expected_sections", [])
        ]
        result = evaluate_case(case, engine, fake_chunks, verbose)
        results.append(result)

    return results


def run_api_eval(
    cases: list[dict],
    engine: SimilarityEngine,
    api_url: str,
    domain_id: str,
    verbose: bool = False,
) -> list[dict]:
    """Run evaluation against the full AVA API pipeline."""
    console.print(f"[blue]Evaluating against AVA API: {api_url}[/blue]")
    console.print(f"[blue]Domain ID: {domain_id}[/blue]")

    results = []
    for case in cases:
        console.print(f"  Retrieving: [cyan]{case['id']}[/cyan]...")
        chunks = retrieve_via_api(api_url, domain_id, case["question"])
        result = evaluate_case(case, engine, chunks, verbose)
        results.append(result)

    return results


def run_direct_eval(
    cases: list[dict],
    engine: SimilarityEngine,
    kb_url: str,
    kb_id: str,
    verbose: bool = False,
) -> list[dict]:
    """Run evaluation directly against ava-kb search."""
    console.print(f"[blue]Evaluating against ava-kb: {kb_url}[/blue]")
    console.print(f"[blue]KB ID: {kb_id}[/blue]")

    results = []
    for case in cases:
        console.print(f"  Retrieving: [cyan]{case['id']}[/cyan]...")
        chunks = retrieve_via_kb_direct(kb_url, kb_id, case["question"])
        result = evaluate_case(case, engine, chunks, verbose)
        results.append(result)

    return results


def chat_query(api_url: str, domain_id: str, query: str) -> tuple[str, dict]:
    """
    Send a query to the chat API and return the full LLM response + diagnostics.
    Parses SSE token events into a complete response string.
    """
    import json as json_mod
    payload = {
        "message": query,
        "context": {"domain": domain_id, "userId": "eval-runner"},
        "conversationId": f"eval-{hash(query) % 100000}",
        "sessionId": "eval-runner-session",
    }
    try:
        resp = requests.post(
            f"{api_url}/api/chat",
            json=payload,
            timeout=120,
            stream=True,
        )
        resp.raise_for_status()
        content = ""
        diagnostics = {}
        for line in resp.iter_lines(decode_unicode=True):
            if not line or not line.startswith("data: "):
                continue
            raw = line[6:]
            try:
                evt = json_mod.loads(raw)
                if evt.get("type") == "token":
                    content += evt.get("data", "")
                elif evt.get("type") == "done":
                    diagnostics = evt.get("data", {}).get("diagnostics", {})
            except (json_mod.JSONDecodeError, KeyError):
                pass
        return content, diagnostics
    except Exception as e:
        console.print(f"[red]Chat API error: {e}[/red]")
        return "", {}


def run_chat_eval(
    cases: list[dict],
    engine: SimilarityEngine,
    api_url: str,
    domain_id: str,
    verbose: bool = False,
) -> list[dict]:
    """
    Run evaluation via the full chat API — scores the LLM's complete response
    against expected content using semantic similarity.
    """
    console.print(f"[blue]Evaluating via chat API: {api_url}[/blue]")
    console.print(f"[blue]Domain: {domain_id}[/blue]")

    results = []
    for case in cases:
        console.print(f"  Querying: [cyan]{case['id']}[/cyan]...")
        response_text, diagnostics = chat_query(api_url, domain_id, case["question"])

        if not response_text:
            results.append({
                "id": case["id"],
                "threshold": case.get("min_similarity", 0.70),
                "best_score": 0,
                "source_match": False,
                "num_chunks_retrieved": 0,
                "passed": False,
                "error": "No response from chat API",
            })
            continue

        # Score the LLM response against each expected section
        # Use hybrid: max(TF-IDF, keyword_presence) to handle paraphrasing
        expected_sections = case.get("expected_sections", [])
        best_tfidf = 0.0
        best_keyword = 0.0
        for section in expected_sections:
            key_content = section.get("key_content", "")
            if not key_content:
                continue
            tfidf_score, _ = engine.compute_best_chunk_similarity([response_text], key_content)
            keyword_score = engine.keyword_presence_score(response_text, key_content)
            best_tfidf = max(best_tfidf, tfidf_score)
            best_keyword = max(best_keyword, keyword_score)

        # Use the better of the two scoring approaches
        best_score = max(best_tfidf, best_keyword)

        if verbose:
            console.print(f"    TF-IDF: {best_tfidf:.3f} | Keywords: {best_keyword:.3f} → Best: {best_score:.3f}")

        threshold = case.get("min_similarity", 0.70)
        passed = best_score >= threshold

        # Check source match from diagnostics
        expected_source = case.get("source_document", "")
        domain_files_used = diagnostics.get("domainFilesUsed", [])
        source_match = any(expected_source in f for f in domain_files_used)

        strategy = diagnostics.get("retrievalStrategy", "unknown")

        if verbose:
            console.print(f"    Score: {best_score:.3f} | Strategy: {strategy}")
            console.print(f"    Response preview: {response_text[:150]}...")

        results.append({
            "id": case["id"],
            "threshold": threshold,
            "best_score": best_score,
            "source_match": source_match,
            "num_chunks_retrieved": len(domain_files_used),
            "passed": passed,
            "strategy": strategy,
        })

    return results


def print_results(results: list[dict]) -> None:
    """Print evaluation results as a formatted table."""
    table = Table(title="Retrieval Evaluation Results", show_lines=True)
    table.add_column("Case ID", style="cyan", max_width=40)
    table.add_column("Score", justify="center", width=8)
    table.add_column("Threshold", justify="center", width=10)
    table.add_column("Source", justify="center", width=8)
    table.add_column("Chunks", justify="center", width=8)
    table.add_column("Result", justify="center", width=8)

    passed_count = 0
    total_count = len(results)

    for r in results:
        score_str = f"{r['best_score']:.3f}" if r.get("best_score") else "N/A"
        threshold_str = f"{r['threshold']:.2f}"
        source_str = "✓" if r.get("source_match") else "✗"
        chunks_str = str(r.get("num_chunks_retrieved", 0))

        if r.get("error"):
            result_str = "[red]ERROR[/red]"
        elif r.get("passed"):
            result_str = "[green]PASS[/green]"
            passed_count += 1
        else:
            result_str = "[red]FAIL[/red]"

        table.add_row(
            r["id"], score_str, threshold_str, source_str, chunks_str, result_str
        )

    console.print()
    console.print(table)
    console.print()

    # Summary
    pass_rate = (passed_count / total_count * 100) if total_count > 0 else 0
    avg_score = (
        sum(r.get("best_score", 0) for r in results) / total_count
        if total_count > 0
        else 0
    )

    summary_color = "green" if pass_rate >= 80 else "yellow" if pass_rate >= 50 else "red"
    console.print(
        Panel(
            f"[{summary_color}]Passed: {passed_count}/{total_count} ({pass_rate:.0f}%)\n"
            f"Average similarity: {avg_score:.3f}[/{summary_color}]",
            title="Summary",
        )
    )

    # Show failures detail
    failures = [r for r in results if not r.get("passed") and not r.get("error")]
    if failures:
        console.print("\n[red]Failed cases:[/red]")
        for f in failures:
            gap = f["threshold"] - f["best_score"]
            console.print(
                f"  • {f['id']}: score={f['best_score']:.3f}, "
                f"needed={f['threshold']:.2f}, gap={gap:.3f}"
            )

    errors = [r for r in results if r.get("error")]
    if errors:
        console.print("\n[red]Errors:[/red]")
        for e in errors:
            console.print(f"  • {e['id']}: {e['error']}")


def main():
    parser = argparse.ArgumentParser(description="AVA Retrieval Evaluation Runner")
    parser.add_argument(
        "--mode",
        choices=["offline", "api", "direct", "chat"],
        default="offline",
        help="Evaluation mode: offline (self-test), api (full pipeline), direct (ava-kb only), chat (full LLM response)",
    )
    parser.add_argument(
        "--api-url",
        default=DEFAULT_AVA_API_URL,
        help=f"AVA API base URL (default: {DEFAULT_AVA_API_URL})",
    )
    parser.add_argument(
        "--kb-url",
        default=DEFAULT_AVA_KB_URL,
        help=f"ava-kb base URL (default: {DEFAULT_AVA_KB_URL})",
    )
    parser.add_argument(
        "--domain-id",
        default=None,
        help="Domain ID to search (required for api mode)",
    )
    parser.add_argument(
        "--kb-id",
        default=None,
        help="Knowledge base ID in ava-kb (required for direct mode)",
    )
    parser.add_argument(
        "--threshold",
        type=float,
        default=None,
        help="Override similarity threshold for all cases",
    )
    parser.add_argument(
        "--model",
        default=DEFAULT_MODEL,
        help=f"Sentence transformer model (default: {DEFAULT_MODEL})",
    )
    parser.add_argument(
        "--backend",
        choices=["auto", "st", "tfidf"],
        default="auto",
        help="Similarity backend: auto (sentence-transformers if available, else tfidf), st, or tfidf",
    )
    parser.add_argument(
        "--case",
        default=None,
        help="Run a single case by ID",
    )
    parser.add_argument(
        "--verbose", "-v",
        action="store_true",
        help="Show per-chunk similarity scores",
    )
    parser.add_argument(
        "--cases-file",
        type=Path,
        default=EVAL_CASES_PATH,
        help=f"Path to eval cases YAML (default: {EVAL_CASES_PATH})",
    )

    args = parser.parse_args()

    # Load cases
    cases = load_cases(args.cases_file, args.case)
    console.print(f"[green]Loaded {len(cases)} eval case(s)[/green]")

    # Apply threshold override
    if args.threshold is not None:
        for case in cases:
            case["min_similarity"] = args.threshold

    # Init similarity engine (sentence-transformers if available, TF-IDF fallback)
    backend = "sentence-transformers" if args.backend == "st" else ("tfidf" if args.backend == "tfidf" else "auto")
    engine = SimilarityEngine(backend=backend, model_name=args.model)

    # Run evaluation based on mode
    if args.mode == "offline":
        results = run_offline_eval(cases, engine, args.verbose)
    elif args.mode == "api":
        if not args.domain_id:
            console.print("[red]--domain-id is required for api mode[/red]")
            sys.exit(1)
        results = run_api_eval(cases, engine, args.api_url, args.domain_id, args.verbose)
    elif args.mode == "direct":
        if not args.kb_id:
            console.print("[red]--kb-id is required for direct mode[/red]")
            sys.exit(1)
        results = run_direct_eval(cases, engine, args.kb_url, args.kb_id, args.verbose)
    elif args.mode == "chat":
        if not args.domain_id:
            console.print("[red]--domain-id is required for chat mode[/red]")
            sys.exit(1)
        results = run_chat_eval(cases, engine, args.api_url, args.domain_id, args.verbose)

    # Print results
    print_results(results)

    # Exit code based on pass rate
    passed = sum(1 for r in results if r.get("passed"))
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
