import { initializeApp } from 'firebase/app';
import { getAuth } from 'firebase/auth';
import { getFirestore } from 'firebase/firestore';
import { getStorage } from 'firebase/storage';

const firebaseConfig = {
  apiKey: 'AIzaSyDtqrlZRoQ5EjBxLdIklHm3RbZPPlywCzQ',
  authDomain: 'fingerprintsystemmbt.firebaseapp.com',
  projectId: 'fingerprintsystemmbt',
  storageBucket: 'fingerprintsystemmbt.firebasestorage.app',
  messagingSenderId: '225601369153',
  appId: '1:225601369153:web:d333a7872cdc026319c2c0',
};

const firebaseApp = initializeApp(firebaseConfig);

export const firebaseAuth = getAuth(firebaseApp);
export const firestore = getFirestore(firebaseApp);
export const storage = getStorage(firebaseApp);
export { firebaseApp };
