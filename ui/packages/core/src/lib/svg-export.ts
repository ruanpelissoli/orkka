// Client-side SVG → PNG rasterization for design-asset downloads. Conversion happens in
// the browser (object URL → <img> → canvas) so neither edition needs a server-side SVG
// renderer or native imaging dependencies. Browser-only — never import from server code.

/** Longest edge of the exported PNG; SVGs are vector so any size stays crisp. */
export const PNG_EXPORT_SIZE = 1024;

const SVG_OPEN_TAG = /<svg\b[^>]*>/i;
const SIZE_ATTRS = /\s(?:width|height)\s*=\s*(?:"[^"]*"|'[^']*')/gi;
const VIEW_BOX = /viewBox\s*=\s*["']\s*[\d.eE+-]+[\s,]+[\d.eE+-]+[\s,]+([\d.eE+-]+)[\s,]+([\d.eE+-]+)\s*["']/i;

/** Width/height ratio from the viewBox, defaulting to square when absent/degenerate. */
function aspectRatioOf(svgText: string): number {
  const viewBox = VIEW_BOX.exec(svgText);
  if (!viewBox) return 1;
  const width = Number(viewBox[1]);
  const height = Number(viewBox[2]);
  return Number.isFinite(width) && Number.isFinite(height) && width > 0 && height > 0
    ? width / height
    : 1;
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("The SVG could not be rendered as an image."));
    img.src = src;
  });
}

/**
 * Rasterizes SVG markup to a PNG blob with the longest edge at `maxDimension`.
 * The root element gets explicit pixel dimensions first — browsers rasterize SVGs
 * without them at an arbitrary intrinsic size (or not at all in Firefox).
 */
export async function svgToPngBlob(
  svgText: string,
  maxDimension: number = PNG_EXPORT_SIZE
): Promise<Blob> {
  const openTag = SVG_OPEN_TAG.exec(svgText)?.[0];
  if (!openTag) throw new Error("Not an SVG document.");

  const aspect = aspectRatioOf(svgText);
  const width = aspect >= 1 ? maxDimension : Math.max(1, Math.round(maxDimension * aspect));
  const height = aspect >= 1 ? Math.max(1, Math.round(maxDimension / aspect)) : maxDimension;

  // Strip any authored width/height (duplicate attributes are an XML parse error), then
  // pin the export size on the root element.
  const sizedOpenTag = openTag
    .replace(SIZE_ATTRS, "")
    .replace(/<svg\b/i, `<svg width="${width}" height="${height}"`);
  const sizedSvg = svgText.replace(openTag, sizedOpenTag);

  const objectUrl = URL.createObjectURL(new Blob([sizedSvg], { type: "image/svg+xml" }));
  try {
    const img = await loadImage(objectUrl);
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Canvas 2D is not available in this browser.");
    context.drawImage(img, 0, 0, width, height);

    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob(
        (blob) => (blob ? resolve(blob) : reject(new Error("PNG encoding failed."))),
        "image/png"
      )
    );
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

/** Triggers a browser download of the blob under the given file name. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  // Revoke on the next tick — revoking synchronously can abort the download in some browsers.
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
