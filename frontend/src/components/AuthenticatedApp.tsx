import { useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { getSystemStatus, type SystemStatus } from "../api";
import AppLayout from "./AppLayout";
import Overview from "./Overview";
import Users from "./Users";
import Security from "./Security";
import Azure from "./Azure";
import SharePoint from "./SharePoint";
import Teams from "./Teams";
import PlaceholderPage from "./PlaceholderPage";
import Settings from "./Settings";
import DatabaseSetupScreen from "./DatabaseSetupScreen";
import { UserProvider } from "../UserContext";
import { CustomerProvider } from "../CustomerContext";
import { IntuneIcon } from "../icons";

export default function AuthenticatedApp() {
  const { instance } = useMsal();
  const [status, setStatus] = useState<SystemStatus | null>(null);

  const checkStatus = () => {
    getSystemStatus(instance)
      .then(setStatus)
      // If the status check itself fails to even reach the backend, treat that
      // the same as "database unreachable" — the setup screen's copy is generic
      // enough to cover both, and there's nothing more specific to show anyway.
      .catch(() => setStatus({ databaseHealthy: false, databaseError: null }));
  };

  useEffect(checkStatus, [instance]);

  if (!status) {
    return null;
  }

  if (!status.databaseHealthy) {
    return <DatabaseSetupScreen status={status} onResolved={checkStatus} />;
  }

  return (
    <UserProvider>
      <CustomerProvider>
        <BrowserRouter>
          <Routes>
            <Route element={<AppLayout />}>
              <Route path="/" element={<Overview />} />
              <Route path="/users" element={<Users />} />
              <Route path="/security" element={<Security />} />
              <Route
                path="/intune"
                element={
                  <PlaceholderPage
                    title="Intune"
                    tagline="Device compliance and management."
                    icon={IntuneIcon}
                  />
                }
              />
              <Route path="/azure" element={<Azure />} />
              <Route path="/sharepoint" element={<SharePoint />} />
              <Route path="/teams" element={<Teams />} />
              <Route path="/settings" element={<Settings />} />
              <Route path="*" element={<Navigate to="/" replace />} />
            </Route>
          </Routes>
        </BrowserRouter>
      </CustomerProvider>
    </UserProvider>
  );
}
