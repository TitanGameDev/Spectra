import LoginButton from "./LoginButton";

export default function LoginScreen() {
  return (
    <div className="centered-viewport">
      <main className="app-shell">
        <div className="brand">
          <div className="brand-mark" />
          <h1>Spectra</h1>
          <p>Sign in to continue</p>
        </div>
        <LoginButton />
      </main>
    </div>
  );
}
