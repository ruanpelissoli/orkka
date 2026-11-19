import { ClientRedirect } from "@/components/page";

/** The local UI has no landing page: the root goes straight to the workspace board. */
export default function RootPage() {
  return <ClientRedirect to="/board" />;
}
