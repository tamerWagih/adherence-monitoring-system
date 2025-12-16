import { Entity, Column, PrimaryGeneratedColumn } from 'typeorm';

/**
 * Employee Personal Info Entity (Minimal)
 * 
 * Used for NT account resolution in event ingestion.
 * Only includes fields needed for NT-based employee identification.
 * 
 * Note: This is a minimal copy for the adherence backend.
 * The full entity exists in people-ops-system, but we use this
 * to avoid cross-repo dependencies while sharing the same database.
 */
@Entity('employee_personal_info')
export class EmployeePersonalInfo {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'employee_id', type: 'uuid', unique: true })
  employeeId: string;

  @Column({ name: 'nt', length: 100, nullable: true })
  nt?: string; // Windows NT account (sam_account_name only, e.g., z.salah.3613)
}
