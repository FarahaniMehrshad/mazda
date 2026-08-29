// This file is intentionally empty.
//
// The old MergePage was step 9 of the 10-step wizard, reconciling the
// separate setup + test scripts into one .spec.ts. The function-catalogue
// architecture doesn't produce those scripts at all — the interpreter
// stitches function bodies into an ephemeral harness at execute time —
// so no merge step is needed. Kept as a placeholder so `git rm` can
// remove it cleanly.

export {};
