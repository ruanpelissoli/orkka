import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Button, BrandMark } from "@/components/ui";

/** The 404 page. Rendered by app/[locale]/not-found.tsx and by pages whose record is gone. */
export function NotFoundView() {
  const t = useTranslations("dashboard");
  return (
    <div className="flex min-h-screen items-center justify-center px-4">
      <div className="text-center">
        {/* The orca swims off into open water — a 404 is a good place for the
            brand to have some personality rather than a bare number. */}
        <BrandMark
          variant="orca"
          alt=""
          className="orkka-swim-logo mx-auto h-24 w-auto opacity-90 sm:h-28"
        />
        <p className="mt-6 text-7xl font-bold gradient-text">404</p>
        <h1 className="mt-4 font-display text-2xl font-bold text-white">
          {t("notFound.title")}
        </h1>
        <p className="mt-2 text-sm text-gray-400">
          {t("notFound.description")}
        </p>
        <div className="mt-8">
          <Link href="/">
            <Button>
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="M19 12H5m0 0l7 7m-7-7l7-7" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              {t("notFound.backToHome")}
            </Button>
          </Link>
        </div>
      </div>
    </div>
  );
}
