import { useCallback } from "react";
import { useNavigate } from "react-router-dom";

/**
 * Where the caller is in the wizard. Determines which steps are
 * addressable by URL:
 *   - signature mode has no run id yet, so step 7 (Run) routes to the
 *     "new run" URL for the signature.
 *   - run mode has both — step 7 routes to the existing run.
 *
 * Pass `null` when the data needed to compute navigation isn't loaded
 * yet (`useQuery` still pending, etc.) — the returned navigator no-ops
 * so clicking a step during loading just does nothing.
 */
export type StepNavigatorContext =
  | { mode: "signature"; signatureId: string }
  | { mode: "run"; runId: string; signatureId: string };

/**
 * Returns a click handler `(step: number) => void` to pass into
 * `StepIndicator`. Each step number maps to its canonical URL:
 *
 *   1  Upload         → /signatures/new
 *   2  Interactable   → /signatures/:id
 *   3  Informational  → /signatures/:id/informational
 *   4  Relationships  → /signatures/:id/relationships
 *   5  User flows     → /signatures/:id/flows
 *   6  Functions      → /signatures/:id/functions
 *   7  Run            → /signatures/:id/run (signature mode)
 *                      /runs/:id           (run mode)
 */
export function useStepNavigator(ctx: StepNavigatorContext | null): (step: number) => void {
  const navigate = useNavigate();
  return useCallback(
    (step: number) => {
      if (!ctx) return;
      const sigId = ctx.signatureId;
      switch (step) {
        case 1:
          // Only navigable from a fresh signature workflow. From a run
          // we'd be abandoning the run id silently — skip instead.
          if (ctx.mode === "signature") navigate("/signatures/new");
          return;
        case 2:
          navigate(`/signatures/${sigId}`);
          return;
        case 3:
          navigate(`/signatures/${sigId}/informational`);
          return;
        case 4:
          navigate(`/signatures/${sigId}/relationships`);
          return;
        case 5:
          navigate(`/signatures/${sigId}/flows`);
          return;
        case 6:
          navigate(`/signatures/${sigId}/functions`);
          return;
        case 7:
          // Run mode goes to the existing run; signature mode starts a
          // fresh one against the same signature. Both land on RunPage
          // which handles the two URL shapes.
          if (ctx.mode === "run") navigate(`/runs/${ctx.runId}`);
          else navigate(`/signatures/${sigId}/run`);
          return;
      }
    },
    [navigate, ctx],
  );
}
