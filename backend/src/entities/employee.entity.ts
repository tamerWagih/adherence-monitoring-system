import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
} from 'typeorm';

/**
 * Employee Entity (Minimal)
 * 
 * Minimal copy of Employee entity for Adherence Backend.
 * Only includes fields needed for adherence relationships.
 * 
 * Note: This is a minimal copy for the adherence backend.
 * The full entity exists in people-ops-system, but we use this
 * to avoid cross-repo dependencies while sharing the same database.
 * 
 * To access nt field, join with EmployeePersonalInfo entity:
 * - employee_personal_info.nt (via employee_id foreign key)
 */
@Entity('employees')
export class Employee {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'hr_id', type: 'int', unique: true, nullable: true })
  hrId?: number;

  @Column({ name: 'full_name_en', length: 255 })
  fullNameEn: string;
}

