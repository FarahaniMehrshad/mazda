import { useEffect } from "react";
import { BrowserRouter, Routes, Route, Navigate } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RequireAuth } from "./RequireAuth";
import { useAuthStore } from "@/features/auth/useAuth";
import { AuthPage } from "@/features/auth/AuthPage";
import { StudioPage } from "@/features/studio/StudioPage";
import { LibraryPage } from "@/features/signatures/LibraryPage";
import { UploadPage } from "@/features/signatures/UploadPage";
import { EditorPage } from "@/features/signatures/EditorPage";
import { InformationalReviewPage } from "@/features/signatures/InformationalReviewPage";
import { RelationshipsPage } from "@/features/signatures/RelationshipsPage";
import { UserFlowsPage } from "@/features/signatures/UserFlowsPage";
import { FunctionsPage } from "@/features/signatures/FunctionsPage";
import { RunPage } from "@/features/generation/RunPage";
import { RunsPage } from "@/features/generation/RunsPage";
import { McpServerPage } from "@/features/mcp/McpServerPage";

const qc = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 30_000, retry: 1, refetchOnWindowFocus: false },
  },
});

export function App() {
  const hydrate = useAuthStore((s) => s.hydrate);
  useEffect(() => {
    hydrate();
  }, [hydrate]);

  return (
    <QueryClientProvider client={qc}>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<AuthPage />} />
          <Route path="/" element={<RequireAuth><StudioPage /></RequireAuth>} />
          <Route path="/signatures" element={<RequireAuth><LibraryPage /></RequireAuth>} />
          <Route path="/signatures/new" element={<RequireAuth><UploadPage /></RequireAuth>} />
          <Route path="/signatures/:id" element={<RequireAuth><EditorPage /></RequireAuth>} />
          <Route path="/signatures/:id/informational" element={<RequireAuth><InformationalReviewPage /></RequireAuth>} />
          <Route path="/signatures/:id/relationships" element={<RequireAuth><RelationshipsPage /></RequireAuth>} />
          <Route path="/signatures/:id/flows" element={<RequireAuth><UserFlowsPage /></RequireAuth>} />
          <Route path="/signatures/:id/functions" element={<RequireAuth><FunctionsPage /></RequireAuth>} />
          {/*
            RunPage handles both entry paths — from a signature (fresh run,
            :sigId in the route) and from an existing run (:id in the route).
            The component reads whichever param is present and behaves
            accordingly.
          */}
          <Route path="/signatures/:sigId/run" element={<RequireAuth><RunPage /></RequireAuth>} />
          <Route path="/runs/:id" element={<RequireAuth><RunPage /></RequireAuth>} />
          <Route path="/runs" element={<RequireAuth><RunsPage /></RequireAuth>} />
          <Route path="/mcp" element={<RequireAuth><McpServerPage /></RequireAuth>} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
