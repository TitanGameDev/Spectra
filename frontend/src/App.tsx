import { AuthenticatedTemplate, UnauthenticatedTemplate } from "@azure/msal-react";
import LoginButton from "./components/LoginButton";
import Welcome from "./components/Welcome";

export default function App() {
  return (
    <main className="app-shell">
      <div className="brand">
        <div className="brand-mark" />
        <h1>Spectra</h1>
        <UnauthenticatedTemplate>
          <p>Sign in to continue</p>
        </UnauthenticatedTemplate>
      </div>
      <UnauthenticatedTemplate>
        <LoginButton />
      </UnauthenticatedTemplate>
      <AuthenticatedTemplate>
        <Welcome />
      </AuthenticatedTemplate>
    </main>
  );
}
