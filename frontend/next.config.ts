import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  output: 'standalone',
  env: {
    NEXT_PUBLIC_API_URL: process.env.NEXT_PUBLIC_API_URL || '',
    NEXT_PUBLIC_APP_NAME:
      process.env.NEXT_PUBLIC_APP_NAME || 'Adherence Monitoring System',
    NEXT_PUBLIC_APP_VERSION: process.env.NEXT_PUBLIC_APP_VERSION || '1.0.0',
  },
  // Reduce build memory usage
  experimental: {
    optimizePackageImports: [
      '@mui/material',
      '@mui/x-date-pickers',
      'recharts',
    ],
  },
};

export default nextConfig;

