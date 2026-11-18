import { features } from "./features";

export default function Home() {
  return (
    <main style={{ fontFamily: "system-ui, sans-serif", padding: 32 }}>
      <h1>Orkka E2E Fixture</h1>
      <p>This app exists so Orkka&apos;s end-to-end tests have something small and predictable to change.</p>
      <ul data-testid="features">
        {features.map((f) => (
          <li key={f}>{f}</li>
        ))}
      </ul>
    </main>
  );
}
