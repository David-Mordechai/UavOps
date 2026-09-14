// Word-level Levenshtein distance / WER, plus a normalizer so trivial casing/punctuation
// differences don't inflate the error rate — the same kind of normalization a real STT
// consumer would apply before comparing to ground truth.
function normalizeWords(text) {
  return (text || "")
    .toLowerCase()
    .replace(/[.,!?;:"']/g, "")
    .trim()
    .split(/\s+/)
    .filter(Boolean);
}

// Returns { wer, substitutions, deletions, insertions, refWords, hypWords }
function computeWer(reference, hypothesis) {
  const ref = normalizeWords(reference);
  const hyp = normalizeWords(hypothesis);
  const n = ref.length;
  const m = hyp.length;

  // dp[i][j] = edit distance between ref[0..i) and hyp[0..j)
  const dp = Array.from({ length: n + 1 }, () => new Array(m + 1).fill(0));
  for (let i = 0; i <= n; i++) dp[i][0] = i;
  for (let j = 0; j <= m; j++) dp[0][j] = j;

  for (let i = 1; i <= n; i++) {
    for (let j = 1; j <= m; j++) {
      if (ref[i - 1] === hyp[j - 1]) {
        dp[i][j] = dp[i - 1][j - 1];
      } else {
        dp[i][j] = 1 + Math.min(
          dp[i - 1][j],     // deletion
          dp[i][j - 1],     // insertion
          dp[i - 1][j - 1]  // substitution
        );
      }
    }
  }

  // Backtrack to classify edits (informational only, not needed for the WER number itself).
  let i = n, j = m, substitutions = 0, deletions = 0, insertions = 0;
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && ref[i - 1] === hyp[j - 1]) {
      i--; j--;
    } else if (i > 0 && j > 0 && dp[i][j] === dp[i - 1][j - 1] + 1) {
      substitutions++; i--; j--;
    } else if (i > 0 && dp[i][j] === dp[i - 1][j] + 1) {
      deletions++; i--;
    } else {
      insertions++; j--;
    }
  }

  const wer = n === 0 ? (m === 0 ? 0 : 1) : dp[n][m] / n;
  return { wer, substitutions, deletions, insertions, refWords: ref, hypWords: hyp };
}
