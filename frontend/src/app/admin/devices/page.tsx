'use client';

import React from 'react';

export default function DevicesPage() {
  return (
    <div>
      <div className="flex justify-between items-center mb-6">
        <h1 className="text-3xl font-bold text-gray-900">
          Device Management
        </h1>
        <button className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700">
          Register New Device
        </button>
      </div>

      <div className="bg-white rounded-lg shadow">
        <div className="p-6">
          <p className="text-gray-600">
            Device list will be displayed here.
          </p>
          <p className="text-sm text-gray-500 mt-2">
            This will show all registered workstations with their status.
          </p>
        </div>
      </div>
    </div>
  );
}

