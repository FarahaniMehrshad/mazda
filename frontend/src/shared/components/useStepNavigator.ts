import { useCallback } from "react";
import { useNavigate } from "react-router-dom";

/**
 * Where the caller is in the wizard. Determines which steps are addressable
 * by URL: signature-only contexts (steps 1–4) don't have a run id yet, so
 * jumping to steps 5–9 is meaningless.
 *
 * Pass `null` when the data needed to compute the navigation isn't loaded
 * yet (`useQuery` still pending, etc.) — the returned navigator no-ops so
 * the user clicking a step during loading just does nothing.
 */
export type StepNavigatorContext =
  | { mode: "signature"; signatureId: string }
  | { mode: "run"; runId: string; signatureId: string };

/**
 * Returns a click handler `(step: number) => void` to pass into `StepIndicator`.
 * Each step number maps to its canonical URL:
 *
 *   1  Upload              → /signatures/new (intentional — Upload only makes
 *                             sense as "create a fresh signature"; from a
 *                             run-mode page this would discard context, so
 *                             we route to the dashboard's upload page only
 *                             when the user is already in a signature
 *                             context. From run mode, step 1 is a no-op.)
 *   2  Interactable        → /signatures/:id
 *   3  Informational       → /signatures/:id/informational
 *   4  User flows          → /signatures/:id/flows
 *   5  Setup               → /runs/:id/setup            (run mode only)
 *   6  Analyze state       → /runs/:id/analyze-state    (run mode only)
 *   7  Test prompt         → /runs/:id/test             (run mode only)
 *   8  Merge & finalize    → /runs/:id/merge            (run mode only)
 *   9  Execution           → /runs/:id/execute          (run mode only)
 */
export function useStepNavigator(ctx: StepNavigatorContext | null): (step: number) => void {
  const navigate = useNavigate();
  return useCallback(
    (step: number) => {
      if (!ctx) return;
      const sigId = ctx.signatureId;
      switch (step) {
        case 1:
          // Only navigable from a fresh signature workflow. From a run we'd
          // be abandoning the run id silently — skip instead.
          if (ctx.mode === "signature") navigate("/signatures/new");
          return;
        case 2:
          navigate(`/signatures/${sigId}`);
          return;
        case 3:
          navigate(`/signatures/${sigId}/informational`);
          return;
        case 4:
          navigate(`/signatures/${sigId}/flows`);
          return;
      }
      if (ctx.mode !== "run") return;
      const runId = ctx.runId;
      switch (step) {
        case 5:
          navigate(`/runs/${runId}/setup`);
          return;
        case 6:
          navigate(`/runs/${runId}/analyze-state`);
          return;
        case 7:
          navigate(`/runs/${runId}/test`);
          return;
        case 8:
          navigate(`/runs/${runId}/merge`);
          return;
        case 9:
          navigate(`/runs/${runId}/execute`);
          return;
      }
    },
    [navigate, ctx],
  );
}
