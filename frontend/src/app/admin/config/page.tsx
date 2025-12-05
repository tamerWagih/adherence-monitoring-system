'use client';

import React from 'react';

export default function ConfigPage() {
  return (
    <div>
      <h1 className="text-3xl font-bold text-gray-900 mb-6">
        Configuration Management
      </h1>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">
            Application Classifications
          </h2>
          <p className="text-gray-600">
            Manage application classification rules.
          </p>
        </div>

        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">
            Workstation Settings
          </h2>
          <p className="text-gray-600">
            Configure workstation settings and defaults.
          </p>
        </div>
      </div>
    </div>
  );
}

