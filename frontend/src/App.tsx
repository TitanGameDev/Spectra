import { AuthenticatedTemplate, UnauthenticatedTemplate } from "@azure/msal-react";
import LoginScreen from "./components/LoginScreen";
import AuthenticatedApp from "./components/AuthenticatedApp";
import ConsentCallback, { isConsentCallback } from "./components/ConsentCallback";

export default function App() {
  if (isConsentCallback()) {
    return <ConsentCallback />;
  }

  return (
    <>
      <UnauthenticatedTemplate>
        <LoginScreen />
      </UnauthenticatedTemplate>
      <AuthenticatedTemplate>
        <AuthenticatedApp />
      </AuthenticatedTemplate>
    </>
  );
}
