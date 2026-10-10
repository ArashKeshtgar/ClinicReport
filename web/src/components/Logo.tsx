/** An ultrasound sector: the fan-shaped image every echo study starts from. */
export function Logo({ size = 26 }: { size?: number }) {
  return (
    <svg className="brand-mark" viewBox="0 0 32 32" width={size} height={size} aria-hidden="true">
      <path d="M16 4 L4.5 22.5 A16 16 0 0 0 27.5 22.5 Z" fill="currentColor" opacity=".18" />
      <path d="M16 4 L4.5 22.5 A16 16 0 0 0 27.5 22.5 Z" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round" />
      <path d="M9.2 20.6 A9 9 0 0 0 22.8 20.6" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <path d="M12.3 15.6 A4.6 4.6 0 0 0 19.7 15.6" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <circle cx="16" cy="4" r="1.8" fill="currentColor" />
    </svg>
  )
}
