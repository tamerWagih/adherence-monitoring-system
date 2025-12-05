import { NextResponse } from 'next/server';

export async function GET() {
  return NextResponse.json({
    status: 'ok',
    timestamp: new Date().toISOString(),
    service: 'Adherence Monitoring Frontend',
    version: process.env.NEXT_PUBLIC_APP_VERSION || '1.0.0',
  });
}

