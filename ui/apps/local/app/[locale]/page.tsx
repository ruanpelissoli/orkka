import { ClientRedirect } from "@/components/page";

/** The local UI has no landing page: the root goes straight to the projects dashboard. */
export default function RootPage() {
  return <ClientRedirect to="/dashboard" />;
}
