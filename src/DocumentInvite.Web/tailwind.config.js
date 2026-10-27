/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        brand: {
          50: '#eff6ff',
          100: '#dbeafe',
          200: '#bfdbfe',
          500: '#3b82f6',
          600: '#2563eb',
          700: '#1d4ed8',
          900: '#1e3a8a',
        },
        surface: { DEFAULT: '#ffffff', muted: '#f8fafc', border: '#e2e8f0' },
        ink: { DEFAULT: '#0f172a', muted: '#64748b' },
      },
      borderRadius: { card: '0.75rem' },
      boxShadow: { card: '0 1px 3px rgba(15, 23, 42, 0.08)' },
    },
  },
  plugins: [],
};
