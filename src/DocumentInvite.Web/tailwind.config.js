/**
 * The single source of truth for the palette. Components use only these semantic tokens.
 * Contrast (WCAG AA, checked): white on brand-600 5.65:1; brand-700 on brand-50 7.22:1;
 * ink on surface-muted 15.3:1; ink-muted on surface-muted 6.3:1;
 * danger-700 on danger-50 6.5:1; success-700 on success-50 6.0:1; warning-700 on warning-50 6.6:1.
 *
 * @type {import('tailwindcss').Config}
 */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        brand: {
          50: '#eef7f5',
          100: '#d5ece7',
          200: '#a9d8cf',
          500: '#1f9a8a',
          600: '#0d7468',
          700: '#0a5c53',
          900: '#0b3b36',
        },
        surface: { DEFAULT: '#ffffff', muted: '#f5f7f6', border: '#d9e0dd' },
        ink: { DEFAULT: '#17211f', muted: '#4f5e5a' },
        danger: { 50: '#fdf0ee', 200: '#f2c4bc', 700: '#a42a1c' },
        success: { 50: '#edf7ee', 200: '#b5dcbc', 700: '#1d6b34' },
        warning: { 50: '#fdf5e3', 200: '#efd596', 700: '#7a4e00' },
      },
      fontFamily: {
        sans: ['"Segoe UI"', 'system-ui', '-apple-system', 'Roboto', '"Helvetica Neue"', 'Arial', 'sans-serif'],
      },
      borderRadius: { card: '0.75rem' },
      boxShadow: { card: '0 1px 2px rgba(23, 33, 31, 0.06)' },
    },
  },
  plugins: [],
};
