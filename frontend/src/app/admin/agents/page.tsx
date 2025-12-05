'use client';

import React from 'react';

export default function AgentsPage() {
  return (
    <div>
      <h1 className="text-3xl font-bold text-gray-900 mb-6">
        Agent Sync Status
      </h1>

      <div className="bg-white rounded-lg shadow">
        <div className="p-6">
          <p className="text-gray-600">
            Agent sync status will be displayed here.
          </p>
          <p className="text-sm text-gray-500 mt-2">
            This will show sync status for all agents with workstations.
          </p>
        </div>
      </div>
    </div>
  );
}

