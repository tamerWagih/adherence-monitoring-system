'use client';

import React from 'react';

export default function HealthPage() {
  return (
    <div>
      <h1 className="text-3xl font-bold text-gray-900 mb-6">
        System Health
      </h1>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-6 mb-6">
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">
            Service Status
          </h2>
          <div className="space-y-2">
            <div className="flex justify-between">
              <span className="text-gray-600">Database</span>
              <span className="text-gray-500">-</span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-600">Redis</span>
              <span className="text-gray-500">-</span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-600">Event Queue</span>
              <span className="text-gray-500">-</span>
            </div>
          </div>
        </div>

        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">
            Metrics
          </h2>
          <div className="space-y-2">
            <div className="flex justify-between">
              <span className="text-gray-600">Events (Last Hour)</span>
              <span className="text-gray-500">-</span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-600">Events (Last 24h)</span>
              <span className="text-gray-500">-</span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-600">Active Workstations</span>
              <span className="text-gray-500">-</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

