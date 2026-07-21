import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { useMsal } from "@azure/msal-react";
import { getCustomerSummaries, type CustomerSummary } from "./api";

const STORAGE_KEY = "spectra.selectedCustomerId";

type CustomerContextValue = {
  customers: CustomerSummary[];
  selectedCustomerId: number | null;
  setSelectedCustomerId: (id: number | null) => void;
  loading: boolean;
  error: string | null;
};

const CustomerContext = createContext<CustomerContextValue>({
  customers: [],
  selectedCustomerId: null,
  setSelectedCustomerId: () => {},
  loading: true,
  error: null,
});

export function CustomerProvider({ children }: { children: ReactNode }) {
  const { instance } = useMsal();
  const [customers, setCustomers] = useState<CustomerSummary[]>([]);
  const [selectedCustomerId, setSelectedCustomerIdState] = useState<number | null>(() => {
    const stored = sessionStorage.getItem(STORAGE_KEY);
    return stored ? Number(stored) : null;
  });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    getCustomerSummaries(instance)
      .then((data) => {
        if (cancelled) return;
        setCustomers(data);
        // Default to the first customer once the list loads, unless a still-valid
        // selection was already restored from a previous session.
        setSelectedCustomerIdState((current) => {
          if (current && data.some((c) => c.id === current)) return current;
          return data[0]?.id ?? null;
        });
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load customers");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance]);

  const setSelectedCustomerId = (id: number | null) => {
    setSelectedCustomerIdState(id);
    if (id) {
      sessionStorage.setItem(STORAGE_KEY, String(id));
    } else {
      sessionStorage.removeItem(STORAGE_KEY);
    }
  };

  return (
    <CustomerContext.Provider value={{ customers, selectedCustomerId, setSelectedCustomerId, loading, error }}>
      {children}
    </CustomerContext.Provider>
  );
}

export function useCustomer() {
  return useContext(CustomerContext);
}
