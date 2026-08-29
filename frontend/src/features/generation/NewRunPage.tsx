// This file is intentionally empty.
//
// The old NewRunPage was a short redirect that created an empty Generation
// against a signature and bounced to /runs/:id/setup. RunPage now handles
// both entry points (from a signature and from an existing run) directly,
// so no separate redirect page is needed. Kept as a placeholder so
// `git rm` can remove it cleanly.

export {};
