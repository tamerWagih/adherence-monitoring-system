import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
} from 'typeorm';

/**
 * User Entity (Minimal)
 * 
 * Minimal copy of User entity for Adherence Backend.
 * Only includes fields needed for exception relationships.
 * 
 * Note: This is a minimal copy for the adherence backend.
 * The full entity exists in people-ops-system, but we use this
 * to avoid cross-repo dependencies while sharing the same database.
 */
@Entity('users')
export class User {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ unique: true })
  email: string;

  @Column()
  firstName: string;

  @Column()
  lastName: string;
}

