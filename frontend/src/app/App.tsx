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
import { TargetPage } from "@/features/target/TargetPage";
import { GenerationPage } from "@/features/generation/GenerationPage";
import { RunsPage } from "@/features/generation/RunsPage";

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
          <Route
            path="/"
            element={
              <RequireAuth>
                <StudioPage />
              </RequireAuth>
            }
          />
          <Route
            path="/signatures"
            element={
              <RequireAuth>
                <LibraryPage />
              </RequireAuth>
            }
          />
          <Route
            path="/signatures/new"
            element={
              <RequireAuth>
                <UploadPage />
              </RequireAuth>
            }
          />
          <Route
            path="/signatures/:id"
            element={
              <RequireAuth>
                <EditorPage />
              </RequireAuth>
            }
          />
          <Route
            path="/target"
            element={
              <RequireAuth>
                <TargetPage />
              </RequireAuth>
            }
          />
          <Route
            path="/runs"
            element={
              <RequireAuth>
                <RunsPage />
              </RequireAuth>
            }
          />
          <Route
            path="/runs/:id"
            element={
              <RequireAuth>
                <GenerationPage />
              </RequireAuth>
            }
          />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
