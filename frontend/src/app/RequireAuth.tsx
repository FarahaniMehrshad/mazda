import { Navigate, useLocation } from "react-router-dom";
import { useShallow } from "zustand/react/shallow";
import { useAuthStore } from "@/features/auth/useAuth";

export function RequireAuth({ children }: { children: React.ReactNode }) {
  const { user, hydrated } = useAuthStore(
    useShallow((s) => ({ user: s.user, hydrated: s.hydrated }))
  );
  const location = useLocation();

  if (!hydrated) return null;
  if (!user) return <Navigate to="/login" state={{ from: location }} replace />;
  return <>{children}</>;
}
