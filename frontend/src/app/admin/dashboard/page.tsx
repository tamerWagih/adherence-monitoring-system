'use client';

import React from 'react';

export default function AdminDashboardPage() {
  return (
    <div>
      <h1 className="text-3xl font-bold text-gray-900 mb-6">
        Admin Dashboard
      </h1>
      
      <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-lg font-semibold text-gray-700 mb-2">
            Total Workstations
          </h2>
          <p className="text-3xl font-bold text-gray-900">-</p>
          <p className="text-sm text-gray-500 mt-2">Loading...</p>
        </div>
        
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-lg font-semibold text-gray-700 mb-2">
            Active Workstations
          </h2>
          <p className="text-3xl font-bold text-green-600">-</p>
          <p className="text-sm text-gray-500 mt-2">Loading...</p>
        </div>
        
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-lg font-semibold text-gray-700 mb-2">
            Events Today
          </h2>
          <p className="text-3xl font-bold text-blue-600">-</p>
          <p className="text-sm text-gray-500 mt-2">Loading...</p>
        </div>
      </div>

      <div className="bg-white p-6 rounded-lg shadow">
        <h2 className="text-xl font-semibold text-gray-900 mb-4">
          Quick Actions
        </h2>
        <div className="space-y-2">
          <p className="text-gray-600">
            • Register new workstation
          </p>
          <p className="text-gray-600">
            • View device status
          </p>
          <p className="text-gray-600">
            • Monitor system health
          </p>
        </div>
      </div>
    </div>
  );
}

