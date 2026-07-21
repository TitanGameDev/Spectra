import { AuthenticatedTemplate, UnauthenticatedTemplate } from "@azure/msal-react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import LoginScreen from "./components/LoginScreen";
import AppLayout from "./components/AppLayout";
import Overview from "./components/Overview";
import Users from "./components/Users";
import PlaceholderPage from "./components/PlaceholderPage";
import Settings from "./components/Settings";
import { UserProvider } from "./UserContext";
import { AzureIcon, IntuneIcon, SecurityIcon } from "./icons";

export default function App() {
  return (
    <>
      <UnauthenticatedTemplate>
        <LoginScreen />
      </UnauthenticatedTemplate>
      <AuthenticatedTemplate>
        <UserProvider>
          <BrowserRouter>
            <Routes>
              <Route element={<AppLayout />}>
                <Route path="/" element={<Overview />} />
                <Route path="/users" element={<Users />} />
                <Route
                  path="/security"
                  element={
                    <PlaceholderPage
                      title="Security"
                      tagline="Alerts and posture across your environment."
                      icon={SecurityIcon}
                    />
                  }
                />
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
                <Route
                  path="/azure"
                  element={
                    <PlaceholderPage title="Azure" tagline="Resources and subscriptions." icon={AzureIcon} />
                  }
                />
                <Route path="/settings" element={<Settings />} />
                <Route path="*" element={<Navigate to="/" replace />} />
              </Route>
            </Routes>
          </BrowserRouter>
        </UserProvider>
      </AuthenticatedTemplate>
    </>
  );
}
