import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { useMsal } from "@azure/msal-react";
import { getMe, type MeResponse } from "./api";

type UserContextValue = {
  me: MeResponse | null;
  loading: boolean;
  error: string | null;
};

const UserContext = createContext<UserContextValue>({ me: null, loading: true, error: null });

export function UserProvider({ children }: { children: ReactNode }) {
  const { instance } = useMsal();
  const [me, setMe] = useState<MeResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    getMe(instance)
      .then((data) => {
        if (!cancelled) setMe(data);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load user");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance]);

  return <UserContext.Provider value={{ me, loading, error }}>{children}</UserContext.Provider>;
}

export function useCurrentUser() {
  return useContext(UserContext);
}
