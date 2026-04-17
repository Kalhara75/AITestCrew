import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Proxy API calls to the .NET WebApi during development so that
    // the frontend can use relative "/api" paths in both dev and production.
    proxy: {
      '/api': {
        target: 'http://localhost:5050',
        changeOrigin: true,
      },
      '/screenshots': {
        target: 'http://localhost:5050',
        changeOrigin: true,
      },
    },
  },
})
