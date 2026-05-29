import { useEffect } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import { ScreenShell } from "@/shared/components/Primitives";
import { useCreateGeneration } from "./api";
import { ApiError } from "@/shared/api/client";

/**
 * Bootstrap route for the wizard. Creates an empty Generation tied to the
 * picked signature, then forwards to /runs/:id/setup.
 */
export function NewRunPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const create = useCreateGeneration();
  const signatureId = params.get("signature") ?? "";

  useEffect(() => {
    if (!signatureId) {
      navigate("/signatures", { replace: true });
      return;
    }
    create
      .mutateAsync(signatureId)
      .then((gen) => navigate(`/runs/${gen.id}/setup`, { replace: true }))
      .catch((err) => {
        alert(err instanceof ApiError ? err.message : "Could not start a new run.");
        navigate("/signatures", { replace: true });
      });
    // We intentionally fire this exactly once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <ScreenShell>
      <TopBar crumb="starting…" />
      <div style={{ padding: 28, color: "var(--text-3)", fontFamily: "var(--f-mono)", fontSize: 13 }}>
        Spinning up a fresh run…
      </div>
    </ScreenShell>
  );
}
